// HarmonyRPC - Reflection-based RPC dispatcher for GameRL

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace GameRL.Harmony.RPC
{
    /// <summary>
    /// Interface for type resolvers that convert string IDs to game objects
    /// </summary>
    public interface ITypeResolver
    {
        /// <summary>
        /// The target type this resolver handles
        /// </summary>
        Type TargetType { get; }

        /// <summary>
        /// Resolve a string ID to the target object
        /// </summary>
        object? Resolve(string id);
    }

    /// <summary>
    /// Reflection-based RPC dispatcher that auto-discovers [GameRLAction] methods
    /// </summary>
    public class HarmonyRPC
    {
        private readonly Dictionary<string, ActionInfo> _actions = new();
        private readonly Dictionary<Type, ITypeResolver> _resolvers = new();
        private readonly Action<string> _log;
        private readonly Action<string> _logError;

        /// <summary>
        /// Information about a registered action
        /// </summary>
        private class ActionInfo
        {
            public string Name { get; set; } = "";
            public MethodInfo Method { get; set; } = null!;
            public ParameterInfo[] Parameters { get; set; } = Array.Empty<ParameterInfo>();
        }

        public HarmonyRPC(Action<string>? log = null, Action<string>? logError = null)
        {
            _log = log ?? Console.WriteLine;
            _logError = logError ?? Console.Error.WriteLine;
        }

        /// <summary>
        /// Register a type resolver for automatic parameter conversion
        /// </summary>
        public void RegisterResolver(ITypeResolver resolver)
        {
            _resolvers[resolver.TargetType] = resolver;
            _log($"[HarmonyRPC] Registered resolver for {resolver.TargetType.Name}");
        }

        /// <summary>
        /// Scan assemblies for [GameRLAction] methods and register them
        /// </summary>
        public void RegisterAll(params Assembly[] assemblies)
        {
            if (assemblies.Length == 0)
            {
                assemblies = new[] { Assembly.GetExecutingAssembly(), Assembly.GetCallingAssembly() };
            }

            foreach (var assembly in assemblies.Distinct())
            {
                RegisterAssembly(assembly);
            }

            _log($"[HarmonyRPC] Registered {_actions.Count} actions");
        }

        private void RegisterAssembly(Assembly assembly)
        {
            try
            {
                var methods = assembly.GetTypes()
                    .Where(t => t.IsClass)  // Include static classes (which are abstract+sealed)
                    .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    .Where(m => m.GetCustomAttribute<GameRLActionAttribute>() != null);

                foreach (var method in methods)
                {
                    var attr = method.GetCustomAttribute<GameRLActionAttribute>()!;
                    var actionName = attr.Name;

                    if (_actions.ContainsKey(actionName))
                    {
                        _logError($"[HarmonyRPC] Duplicate action name: {actionName}");
                        continue;
                    }

                    _actions[actionName] = new ActionInfo
                    {
                        Name = actionName,
                        Method = method,
                        Parameters = method.GetParameters()
                    };

                    _log($"[HarmonyRPC] Registered action: {actionName} -> {method.DeclaringType?.Name}.{method.Name}");
                }
            }
            catch (Exception ex)
            {
                _logError($"[HarmonyRPC] Failed to scan assembly {assembly.GetName().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Dispatch an action by name with the given parameters
        /// </summary>
        /// <returns>True if action was found and executed, false otherwise</returns>
        public bool Dispatch(string actionName, Dictionary<string, object>? parameters)
        {
            if (!_actions.TryGetValue(actionName, out var actionInfo))
            {
                _logError($"[HarmonyRPC] Unknown action: {actionName}");
                return false;
            }

            try
            {
                var args = BindParameters(actionInfo, parameters ?? new Dictionary<string, object>());
                actionInfo.Method.Invoke(null, args);
                return true;
            }
            catch (TargetInvocationException ex)
            {
                _logError($"[HarmonyRPC] Action '{actionName}' threw exception: {ex.InnerException?.Message ?? ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                _logError($"[HarmonyRPC] Failed to dispatch '{actionName}': {ex.Message}");
                return false;
            }
        }

        private object?[] BindParameters(ActionInfo actionInfo, Dictionary<string, object> parameters)
        {
            var args = new object?[actionInfo.Parameters.Length];

            for (int i = 0; i < actionInfo.Parameters.Length; i++)
            {
                var param = actionInfo.Parameters[i];
                var paramType = param.ParameterType;

                // Get the JSON key name (from attribute or parameter name)
                var paramAttr = param.GetCustomAttribute<GameRLParamAttribute>();
                var jsonKey = paramAttr?.Name ?? param.Name ?? $"arg{i}";

                // Check if we have a value for this parameter
                if (!parameters.TryGetValue(jsonKey, out var rawValue))
                {
                    // Use default value if available, otherwise null
                    args[i] = param.HasDefaultValue ? param.DefaultValue : GetDefaultValue(paramType);
                    continue;
                }

                // Check if this parameter needs resolution (string ID -> game object)
                var needsResolve = param.GetCustomAttribute<ResolveAttribute>() != null;
                if (needsResolve && rawValue is string stringId && _resolvers.TryGetValue(paramType, out var resolver))
                {
                    args[i] = resolver.Resolve(stringId);
                    continue;
                }

                // Also check if we have a resolver for this type even without [Resolve] attribute
                // (allows implicit resolution for known types like Pawn)
                if (rawValue is string id && _resolvers.TryGetValue(paramType, out var implicitResolver))
                {
                    args[i] = implicitResolver.Resolve(id);
                    continue;
                }

                // Standard type conversion
                args[i] = ConvertValue(rawValue, paramType);
            }

            return args;
        }

        private object? ConvertValue(object? value, Type targetType)
        {
            if (value == null)
                return GetDefaultValue(targetType);

            var valueType = value.GetType();

            // Already correct type
            if (targetType.IsAssignableFrom(valueType))
                return value;

            // Handle nullable types
            var underlyingType = Nullable.GetUnderlyingType(targetType);
            if (underlyingType != null)
            {
                targetType = underlyingType;
            }

            // Numeric conversions
            if (targetType == typeof(int))
                return Convert.ToInt32(value);
            if (targetType == typeof(long))
                return Convert.ToInt64(value);
            if (targetType == typeof(float))
                return Convert.ToSingle(value);
            if (targetType == typeof(double))
                return Convert.ToDouble(value);
            if (targetType == typeof(bool))
                return Convert.ToBoolean(value);
            if (targetType == typeof(string))
                return value.ToString();

            // Enum conversion from string
            if (targetType.IsEnum && value is string enumStr)
                return Enum.Parse(targetType, enumStr, ignoreCase: true);

            // Last resort: try direct cast
            return Convert.ChangeType(value, targetType);
        }

        private static object? GetDefaultValue(Type type)
        {
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        /// <summary>
        /// Get list of registered action names (for introspection)
        /// </summary>
        public IEnumerable<string> GetActionNames() => _actions.Keys;

        /// <summary>
        /// Check if an action is registered
        /// </summary>
        public bool HasAction(string actionName) => _actions.ContainsKey(actionName);
    }
}
