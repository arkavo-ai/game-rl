//! Streamable HTTP transport for MCP JSON-RPC
//!
//! Implements the MCP 2025-11-25 Streamable HTTP transport:
//! - POST /mcp — JSON-RPC request/response
//! - GET /mcp  — SSE stream for server-initiated notifications
//! - DELETE /mcp — terminate session

use crate::GameRLServer;
use crate::environment::GameEnvironment;
use crate::handler;
use crate::mcp::{Notification, Request};
use axum::extract::State;
use axum::http::{HeaderMap, HeaderName, Method, StatusCode};
use axum::response::sse::{Event, KeepAlive, Sse};
use axum::response::{IntoResponse, Json};
use axum::routing::get;
use axum::Router;
use game_rl_core::{AgentId, Result};
use std::collections::HashMap;
use std::convert::Infallible;
use std::net::SocketAddr;
use std::sync::Arc;
use std::time::Instant;
use tokio::sync::RwLock;
use tokio_stream::wrappers::BroadcastStream;
use tokio_stream::StreamExt;
use tower_http::cors::{Any, CorsLayer};
use tracing::{debug, info, warn};

const SESSION_HEADER: &str = "mcp-session-id";

/// Session state for an HTTP client
struct Session {
    last_activity: Instant,
    /// Agent IDs registered through this session (for cleanup)
    agent_ids: Vec<AgentId>,
}

/// Manages active sessions
struct SessionManager {
    sessions: HashMap<String, Session>,
}

impl SessionManager {
    fn new() -> Self {
        Self {
            sessions: HashMap::new(),
        }
    }

    fn create(&mut self) -> String {
        let id = uuid::Uuid::new_v4().to_string();
        let session = Session {
            last_activity: Instant::now(),
            agent_ids: Vec::new(),
        };
        self.sessions.insert(id.clone(), session);
        id
    }

    fn get_mut(&mut self, id: &str) -> Option<&mut Session> {
        self.sessions.get_mut(id)
    }

    fn remove(&mut self, id: &str) -> Option<Session> {
        self.sessions.remove(id)
    }

    fn exists(&self, id: &str) -> bool {
        self.sessions.contains_key(id)
    }
}

/// Shared state for HTTP handlers
struct HttpState<E: GameEnvironment> {
    server: GameRLServer<E>,
    sessions: RwLock<SessionManager>,
}

/// Run the MCP server on Streamable HTTP
pub async fn run<E: GameEnvironment>(server: GameRLServer<E>, addr: SocketAddr) -> Result<()> {
    let state = Arc::new(HttpState {
        server,
        sessions: RwLock::new(SessionManager::new()),
    });

    let cors = CorsLayer::new()
        .allow_origin(Any)
        .allow_methods([Method::GET, Method::POST, Method::DELETE])
        .allow_headers([
            axum::http::header::CONTENT_TYPE,
            HeaderName::from_static(SESSION_HEADER),
        ])
        .expose_headers([HeaderName::from_static(SESSION_HEADER)]);

    let app = Router::new()
        .route(
            "/mcp",
            get(handle_get_sse::<E>)
                .post(handle_post::<E>)
                .delete(handle_delete::<E>),
        )
        .layer(cors)
        .with_state(state);

    info!("Game-RL MCP HTTP server listening on {}", addr);

    let listener = tokio::net::TcpListener::bind(addr)
        .await
        .map_err(|e| game_rl_core::GameRLError::IpcError(format!("Failed to bind: {}", e)))?;

    axum::serve(listener, app)
        .await
        .map_err(|e| game_rl_core::GameRLError::IpcError(format!("Server error: {}", e)))?;

    Ok(())
}

/// Extract session ID from headers
fn get_session_id(headers: &HeaderMap) -> Option<String> {
    headers
        .get(SESSION_HEADER)
        .and_then(|v| v.to_str().ok())
        .map(|s| s.to_string())
}

/// POST /mcp — handle JSON-RPC request
async fn handle_post<E: GameEnvironment>(
    State(state): State<Arc<HttpState<E>>>,
    headers: HeaderMap,
    Json(request): Json<Request>,
) -> impl IntoResponse {
    debug!("HTTP POST /mcp: method={}", request.method);

    // For initialize requests, create a new session
    if request.method == "initialize" {
        let response = handler::handle_request(&request, &state.server).await;
        let session_id = {
            let mut sessions = state.sessions.write().await;
            sessions.create()
        };
        debug!("Created session: {}", session_id);

        let mut headers = HeaderMap::new();
        headers.insert(
            HeaderName::from_static(SESSION_HEADER),
            session_id.parse().unwrap(),
        );
        return (StatusCode::OK, headers, Json(response));
    }

    // For all other requests, validate the session
    let session_id = match get_session_id(&headers) {
        Some(id) => id,
        None => {
            let response = crate::mcp::Response::error(
                request.id.clone(),
                -32600,
                "Missing Mcp-Session-Id header. Call initialize first.",
            );
            return (StatusCode::BAD_REQUEST, HeaderMap::new(), Json(response));
        }
    };

    {
        let mut sessions = state.sessions.write().await;
        match sessions.get_mut(&session_id) {
            Some(session) => {
                session.last_activity = Instant::now();
            }
            None => {
                let response = crate::mcp::Response::error(
                    request.id.clone(),
                    -32600,
                    "Unknown session. Call initialize first.",
                );
                return (StatusCode::NOT_FOUND, HeaderMap::new(), Json(response));
            }
        }
    }

    let response = handler::handle_request(&request, &state.server).await;

    // Track agent registrations for session cleanup
    if request.method == "tools/call" {
        if let Some(name) = request.params.get("name").and_then(|v| v.as_str()) {
            if name == "registerAgent" || name == "register_agent" {
                if let Some(args) = request.params.get("arguments") {
                    if let Some(agent_id) = args
                        .get("AgentId")
                        .and_then(|v| v.as_str())
                    {
                        if response.error.is_none() {
                            let mut sessions = state.sessions.write().await;
                            if let Some(session) = sessions.get_mut(&session_id) {
                                session.agent_ids.push(agent_id.to_string());
                            }
                        }
                    }
                }
            }
        }
    }

    let mut resp_headers = HeaderMap::new();
    resp_headers.insert(
        HeaderName::from_static(SESSION_HEADER),
        session_id.parse().unwrap(),
    );
    (StatusCode::OK, resp_headers, Json(response))
}

/// GET /mcp — SSE stream for server-initiated notifications
async fn handle_get_sse<E: GameEnvironment>(
    State(state): State<Arc<HttpState<E>>>,
    headers: HeaderMap,
) -> std::result::Result<impl IntoResponse, StatusCode> {
    let session_id = match get_session_id(&headers) {
        Some(id) => id,
        None => return Err(StatusCode::BAD_REQUEST),
    };

    {
        let sessions = state.sessions.read().await;
        if !sessions.exists(&session_id) {
            return Err(StatusCode::NOT_FOUND);
        }
    }

    // Subscribe to game events
    let event_rx = {
        let env = state.server.environment().read().await;
        env.subscribe_events()
    };

    let boxed_stream: std::pin::Pin<
        Box<dyn tokio_stream::Stream<Item = std::result::Result<Event, Infallible>> + Send>,
    > = match event_rx {
        Some(rx) => {
            let broadcast_stream = BroadcastStream::new(rx);
            let mapped = broadcast_stream.filter_map(|result| match result {
                Ok(update) => {
                    let notification =
                        Notification::state_update(update.tick, update.state, update.events);
                    match serde_json::to_string(&notification) {
                        Ok(json) => Some(Ok::<_, Infallible>(Event::default().data(json))),
                        Err(e) => {
                            warn!("Failed to serialize SSE event: {}", e);
                            None
                        }
                    }
                }
                Err(e) => {
                    warn!("Broadcast lag: {}", e);
                    None
                }
            });
            Box::pin(mapped)
        }
        None => {
            // No events supported — return an empty stream that stays open
            let pending =
                tokio_stream::pending::<std::result::Result<Event, Infallible>>();
            Box::pin(pending)
        }
    };

    Ok(Sse::new(boxed_stream).keep_alive(KeepAlive::default()))
}

/// DELETE /mcp — terminate session and clean up agents
async fn handle_delete<E: GameEnvironment>(
    State(state): State<Arc<HttpState<E>>>,
    headers: HeaderMap,
) -> impl IntoResponse {
    let session_id = match get_session_id(&headers) {
        Some(id) => id,
        None => return StatusCode::BAD_REQUEST,
    };

    let session = {
        let mut sessions = state.sessions.write().await;
        sessions.remove(&session_id)
    };

    let Some(session) = session else {
        return StatusCode::NOT_FOUND;
    };

    // Deregister all agents from this session
    for agent_id in &session.agent_ids {
        {
            let mut env = state.server.environment().write().await;
            let _ = env.deregister_agent(agent_id).await;
        }
        {
            let mut reg = state.server.registry().write().await;
            let _ = reg.deregister(agent_id);
        }
    }

    info!(
        "Session {} terminated, cleaned up {} agents",
        session_id,
        session.agent_ids.len()
    );

    StatusCode::OK
}
