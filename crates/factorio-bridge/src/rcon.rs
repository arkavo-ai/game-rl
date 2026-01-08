//! Source RCON protocol client for Factorio
//!
//! Implements the Valve Source RCON protocol used by Factorio's headless server.
//! Protocol spec: https://developer.valvesoftware.com/wiki/Source_RCON_Protocol

use game_rl_core::{GameRLError, Result};
use std::sync::atomic::{AtomicI32, Ordering};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpStream;
use tokio::sync::Mutex;
use tracing::{debug, info, warn};

/// RCON packet type constants
pub mod packet_type {
    /// Authentication response / Execute command (context-dependent)
    pub const EXEC_COMMAND: i32 = 2;
    /// Authenticate with password
    pub const AUTH: i32 = 3;
}

/// RCON packet types for creating packets
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum PacketType {
    /// Execute a command
    ExecCommand,
    /// Authenticate with password
    Auth,
}

impl PacketType {
    /// Get the wire protocol value
    pub fn as_i32(self) -> i32 {
        match self {
            PacketType::ExecCommand => packet_type::EXEC_COMMAND,
            PacketType::Auth => packet_type::AUTH,
        }
    }
}

/// A single RCON packet
#[derive(Debug)]
pub struct RconPacket {
    pub id: i32,
    pub packet_type: i32,
    pub body: String,
}

impl RconPacket {
    /// Create a new packet
    pub fn new(id: i32, packet_type: PacketType, body: impl Into<String>) -> Self {
        Self {
            id,
            packet_type: packet_type.as_i32(),
            body: body.into(),
        }
    }

    /// Serialize packet to bytes
    pub fn to_bytes(&self) -> Vec<u8> {
        let body_bytes = self.body.as_bytes();
        // Size = id(4) + type(4) + body + null(1) + null(1)
        let size = 4 + 4 + body_bytes.len() + 2;

        let mut buf = Vec::with_capacity(4 + size);
        buf.extend_from_slice(&(size as i32).to_le_bytes());
        buf.extend_from_slice(&self.id.to_le_bytes());
        buf.extend_from_slice(&self.packet_type.to_le_bytes());
        buf.extend_from_slice(body_bytes);
        buf.push(0); // Body null terminator
        buf.push(0); // Packet null terminator

        buf
    }

    /// Parse packet from bytes (excluding size prefix)
    pub fn from_bytes(data: &[u8]) -> Result<Self> {
        if data.len() < 10 {
            return Err(GameRLError::ProtocolError(
                "RCON packet too short".to_string(),
            ));
        }

        let id = i32::from_le_bytes([data[0], data[1], data[2], data[3]]);
        let packet_type = i32::from_le_bytes([data[4], data[5], data[6], data[7]]);

        // Body is everything after type until the first null
        let body_end = data[8..]
            .iter()
            .position(|&b| b == 0)
            .unwrap_or(data.len() - 8);
        let body = String::from_utf8_lossy(&data[8..8 + body_end]).to_string();

        Ok(Self {
            id,
            packet_type,
            body,
        })
    }
}

/// RCON client for communicating with Factorio headless server
pub struct RconClient {
    /// TCP stream to RCON server
    stream: Mutex<Option<TcpStream>>,
    /// Server address
    address: String,
    /// Authentication password
    password: String,
    /// Next packet ID
    next_id: AtomicI32,
    /// Whether authenticated
    authenticated: std::sync::atomic::AtomicBool,
}

impl RconClient {
    /// Create a new RCON client
    pub fn new(address: impl Into<String>, password: impl Into<String>) -> Self {
        Self {
            stream: Mutex::new(None),
            address: address.into(),
            password: password.into(),
            next_id: AtomicI32::new(1),
            authenticated: std::sync::atomic::AtomicBool::new(false),
        }
    }

    /// Connect and authenticate with the RCON server
    pub async fn connect(&self) -> Result<()> {
        info!("Connecting to RCON at {}", self.address);

        let stream = TcpStream::connect(&self.address)
            .await
            .map_err(|e| GameRLError::IpcError(format!("RCON connect failed: {}", e)))?;

        *self.stream.lock().await = Some(stream);

        // Authenticate
        let auth_id = self.next_id.fetch_add(1, Ordering::SeqCst);
        let auth_packet = RconPacket::new(auth_id, PacketType::Auth, &self.password);

        self.send_packet(&auth_packet).await?;

        // Read auth response
        let response = self.recv_packet().await?;

        if response.id == -1 {
            self.authenticated.store(false, Ordering::SeqCst);
            return Err(GameRLError::IpcError(
                "RCON authentication failed".to_string(),
            ));
        }

        if response.id != auth_id {
            warn!(
                "RCON auth response ID mismatch: expected {}, got {}",
                auth_id, response.id
            );
        }

        self.authenticated.store(true, Ordering::SeqCst);
        info!("RCON authenticated successfully");

        Ok(())
    }

    /// Mark as disconnected (call on error)
    fn mark_disconnected(&self) {
        self.authenticated.store(false, Ordering::SeqCst);
    }

    /// Check if connected and authenticated
    pub fn is_connected(&self) -> bool {
        self.authenticated.load(Ordering::SeqCst)
    }

    /// Execute a command and return the response (with auto-reconnect)
    pub async fn execute(&self, command: &str) -> Result<String> {
        // Try once, reconnect on failure, try again
        match self.execute_inner(command).await {
            Ok(response) => Ok(response),
            Err(e) => {
                // Check if it's a connection error
                let err_str = e.to_string();
                if err_str.contains("Broken pipe")
                    || err_str.contains("eof")
                    || err_str.contains("not connected")
                    || err_str.contains("not authenticated")
                {
                    warn!("RCON connection lost, reconnecting...");
                    self.mark_disconnected();
                    self.connect().await?;
                    self.execute_inner(command).await
                } else {
                    Err(e)
                }
            }
        }
    }

    /// Inner execute without reconnect logic
    async fn execute_inner(&self, command: &str) -> Result<String> {
        if !self.authenticated.load(Ordering::SeqCst) {
            return Err(GameRLError::IpcError("RCON not authenticated".to_string()));
        }

        let cmd_id = self.next_id.fetch_add(1, Ordering::SeqCst);
        let packet = RconPacket::new(cmd_id, PacketType::ExecCommand, command);

        debug!("RCON exec: {}", command);
        self.send_packet(&packet).await?;

        // Read single response packet
        // Note: For large responses, Factorio may split across packets,
        // but for our use case single packets should suffice
        let response_packet = self.recv_packet().await?;

        if response_packet.id != cmd_id {
            debug!(
                "Response ID mismatch: expected {}, got {}",
                cmd_id, response_packet.id
            );
        }

        debug!(
            "RCON response: {}",
            &response_packet.body[..response_packet.body.len().min(100)]
        );
        Ok(response_packet.body)
    }

    /// Execute a Lua command via /c
    ///
    /// Note: First /c command in a session triggers achievement warning,
    /// but in headless mode with mods this is typically not an issue.
    /// Empty responses are normal for commands that use rcon.print().
    pub async fn lua(&self, lua_code: &str) -> Result<String> {
        let command = format!("/c {}", lua_code);
        self.execute(&command).await
    }

    /// Call a remote interface function
    pub async fn remote_call(&self, interface: &str, func: &str, args: &str) -> Result<String> {
        let lua = format!("remote.call(\"{}\", \"{}\", {})", interface, func, args);
        self.lua(&lua).await
    }

    /// Send a packet
    async fn send_packet(&self, packet: &RconPacket) -> Result<()> {
        let mut guard = self.stream.lock().await;
        let stream = guard
            .as_mut()
            .ok_or_else(|| GameRLError::IpcError("RCON not connected".to_string()))?;

        let bytes = packet.to_bytes();
        stream
            .write_all(&bytes)
            .await
            .map_err(|e| GameRLError::IpcError(format!("RCON send failed: {}", e)))?;

        Ok(())
    }

    /// Receive a packet
    async fn recv_packet(&self) -> Result<RconPacket> {
        let mut guard = self.stream.lock().await;
        let stream = guard
            .as_mut()
            .ok_or_else(|| GameRLError::IpcError("RCON not connected".to_string()))?;

        // Read size (4 bytes, little endian)
        let mut size_buf = [0u8; 4];
        stream
            .read_exact(&mut size_buf)
            .await
            .map_err(|e| GameRLError::IpcError(format!("RCON recv size failed: {}", e)))?;
        let size = i32::from_le_bytes(size_buf) as usize;

        if size > 4096 {
            return Err(GameRLError::ProtocolError(format!(
                "RCON packet too large: {} bytes",
                size
            )));
        }

        // Read packet body
        let mut data = vec![0u8; size];
        stream
            .read_exact(&mut data)
            .await
            .map_err(|e| GameRLError::IpcError(format!("RCON recv body failed: {}", e)))?;

        RconPacket::from_bytes(&data)
    }

    /// Disconnect from the server
    pub async fn disconnect(&self) {
        if let Some(mut stream) = self.stream.lock().await.take() {
            let _ = stream.shutdown().await;
        }
        self.authenticated.store(false, Ordering::SeqCst);
        info!("RCON disconnected");
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_packet_serialization() {
        let packet = RconPacket::new(1, PacketType::Auth, "password123");
        let bytes = packet.to_bytes();

        // Size should be 4 + 4 + 11 + 2 = 21
        assert_eq!(bytes.len(), 4 + 21);

        // Check size field
        let size = i32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]);
        assert_eq!(size, 21);

        // Check packet type
        let ptype = i32::from_le_bytes([bytes[8], bytes[9], bytes[10], bytes[11]]);
        assert_eq!(ptype, PacketType::Auth.as_i32());
    }

    #[test]
    fn test_packet_deserialization() {
        let original = RconPacket::new(42, PacketType::ExecCommand, "test command");
        let bytes = original.to_bytes();

        // Skip size prefix (first 4 bytes)
        let parsed = RconPacket::from_bytes(&bytes[4..]).unwrap();

        assert_eq!(parsed.id, 42);
        assert_eq!(parsed.packet_type, PacketType::ExecCommand.as_i32());
        assert_eq!(parsed.body, "test command");
    }
}
