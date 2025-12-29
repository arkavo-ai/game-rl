// GameRL Action Attribute for RPC dispatch

using System;

namespace GameRL.Harmony.RPC
{
    /// <summary>
    /// Marks a method as a GameRL action that can be invoked via RPC.
    /// The method will be auto-discovered and registered at startup.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class GameRLActionAttribute : Attribute
    {
        /// <summary>
        /// The action name used in the wire protocol (e.g., "move", "set_priority")
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Optional description for documentation/introspection
        /// </summary>
        public string? Description { get; set; }

        public GameRLActionAttribute(string name)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
        }
    }

    /// <summary>
    /// Marks a class as containing GameRL actions to be scanned
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class GameRLComponentAttribute : Attribute
    {
    }
}
