// GameRL Parameter Attributes for RPC dispatch

using System;

namespace GameRL.Harmony.RPC
{
    /// <summary>
    /// Maps a method parameter to a specific JSON key name.
    /// If not specified, the parameter name is used as the key.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
    public class GameRLParamAttribute : Attribute
    {
        /// <summary>
        /// The JSON key name to read from the params dictionary
        /// </summary>
        public string Name { get; }

        public GameRLParamAttribute(string name)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
        }
    }

    /// <summary>
    /// Marks a parameter for automatic resolution from string ID to game object.
    /// The HarmonyRPC system will use registered resolvers to convert the string
    /// to the appropriate game object type (e.g., Pawn, Thing, Building).
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
    public class ResolveAttribute : Attribute
    {
    }
}
