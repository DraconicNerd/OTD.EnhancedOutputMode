using System;
using System.Numerics;
using System.Reflection;
using OpenTabletDriver.Plugin;

namespace OTD.EnhancedOutputMode.Lib.Tools
{
    /// <summary>
    /// Helper class to detect Monitor Toggle plugin state via reflection.
    /// This allows touch coordinates to be offset to match pen coordinates when Monitor Toggle is active.
    /// </summary>
    public static class MonitorToggleDetector
    {
        private static bool _initialized;
        private static bool _isAvailable;
        private static PropertyInfo? _isActiveProperty;
        private static FieldInfo? _offsetField;
        private static FieldInfo? _multiplierField;

        /// <summary>
        /// Indicates whether Monitor Toggle plugin was detected
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                EnsureInitialized();
                return _isAvailable;
            }
        }

        /// <summary>
        /// Gets the current offset from Monitor Toggle, or Vector2.Zero if not active/available
        /// </summary>
        public static Vector2 GetOffset()
        {
            EnsureInitialized();

            if (!_isAvailable)
                return Vector2.Zero;

            try
            {
                // Check if Monitor Toggle is currently active
                bool isActive = (bool?)_isActiveProperty?.GetValue(null) ?? false;
                if (!isActive)
                    return Vector2.Zero;

                // Get the offset
                return (Vector2?)_offsetField?.GetValue(null) ?? Vector2.Zero;
            }
            catch
            {
                return Vector2.Zero;
            }
        }

        /// <summary>
        /// Gets the current multiplier from Monitor Toggle, or Vector2.One if not active/available
        /// </summary>
        public static Vector2 GetMultiplier()
        {
            EnsureInitialized();

            if (!_isAvailable)
                return Vector2.One;

            try
            {
                // Check if Monitor Toggle is currently active
                bool isActive = (bool?)_isActiveProperty?.GetValue(null) ?? false;
                if (!isActive)
                    return Vector2.One;

                // Get the multiplier
                return (Vector2?)_multiplierField?.GetValue(null) ?? Vector2.One;
            }
            catch
            {
                return Vector2.One;
            }
        }

        private static void EnsureInitialized()
        {
            if (_initialized)
                return;

            _initialized = true;
            _isAvailable = false;

            try
            {
                // Try to find the Monitor Toggle binding type across all loaded assemblies
                Type? bindingType = null;
                
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        bindingType = assembly.GetType("monitor_toggle.monitor_toggle_binding");
                        if (bindingType != null)
                            break;
                    }
                    catch
                    {
                        // Skip assemblies that can't be searched
                    }
                }

                if (bindingType == null)
                {
                    Log.Write("MonitorToggleDetector", "Monitor Toggle plugin not detected");
                    return;
                }

                // Get the static fields/properties
                _isActiveProperty = bindingType.GetProperty("is_active", 
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                _offsetField = bindingType.GetField("offset", 
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                _multiplierField = bindingType.GetField("multiplier", 
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

                if (_isActiveProperty != null && _offsetField != null)
                {
                    _isAvailable = true;
                    Log.Write("MonitorToggleDetector", "Monitor Toggle plugin detected and integrated");
                }
                else
                {
                    Log.Write("MonitorToggleDetector", "Monitor Toggle plugin found but internal structure not recognized", LogLevel.Warning);
                }
            }
            catch (Exception ex)
            {
                Log.Write("MonitorToggleDetector", $"Error detecting Monitor Toggle: {ex.Message}", LogLevel.Warning);
            }
        }

        /// <summary>
        /// Resets the detector state (useful for testing or if plugins are reloaded)
        /// </summary>
        public static void Reset()
        {
            _initialized = false;
            _isAvailable = false;
            _isActiveProperty = null;
            _offsetField = null;
            _multiplierField = null;
        }
    }
}
