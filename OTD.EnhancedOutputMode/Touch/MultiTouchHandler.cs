using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Platform.Display;
using OpenTabletDriver.Plugin.Tablet.Touch;
using OTD.EnhancedOutputMode.Lib.Tools;

namespace OTD.EnhancedOutputMode.Touch
{
    /// <summary>
    /// Handles multi-touch input by transforming tablet touch points and injecting them via Windows Touch Injection API
    /// </summary>
    public class MultiTouchHandler
    {
        private readonly IVirtualScreen _virtualScreen;
        private readonly Dictionary<int, TouchState> _activeTouches = new(); // Key: tablet TouchID
        private readonly Dictionary<int, uint> _touchIdToPointerId = new(); // Maps tablet TouchID to Windows pointerId (1-10)
        private readonly bool[] _usedPointerIds = new bool[MaxContacts + 1]; // Track which Windows pointer IDs are in use (1-10)
        private readonly object _lock = new();
        private int _consecutiveFailures = 0;
        private const int MaxFailuresBeforeReset = 5;

        // Screen bounds cached from IVirtualScreen
        private readonly int _screenMinX;
        private readonly int _screenMinY;
        private readonly int _screenMaxX;
        private readonly int _screenMaxY;

        /// <summary>
        /// Maximum number of simultaneous touch contacts supported
        /// </summary>
        public const int MaxContacts = 10;

        public MultiTouchHandler(IVirtualScreen virtualScreen)
        {
            _virtualScreen = virtualScreen ?? throw new ArgumentNullException(nameof(virtualScreen));
            
            // Cache screen bounds for clamping
            _screenMinX = (int)_virtualScreen.Position.X;
            _screenMinY = (int)_virtualScreen.Position.Y;
            _screenMaxX = _screenMinX + (int)_virtualScreen.Width - 1;
            _screenMaxY = _screenMinY + (int)_virtualScreen.Height - 1;

            Log.Write("MultiTouchHandler", $"Screen bounds: ({_screenMinX},{_screenMinY}) to ({_screenMaxX},{_screenMaxY})");
            
            if (!TouchInjector.Initialize((uint)MaxContacts))
            {
                Log.Write("MultiTouchHandler", "Failed to initialize touch injection", LogLevel.Error);
            }
        }

        private uint AllocatePointerId(int tabletTouchId)
        {
            if (_touchIdToPointerId.TryGetValue(tabletTouchId, out var existingId))
                return existingId;

            // Find first available pointer ID (1-10, not 0)
            for (uint i = 1; i <= MaxContacts; i++)
            {
                if (!_usedPointerIds[i])
                {
                    _usedPointerIds[i] = true;
                    _touchIdToPointerId[tabletTouchId] = i;
                    return i;
                }
            }
            return 1; // Fallback
        }

        private void ReleasePointerId(int tabletTouchId)
        {
            if (_touchIdToPointerId.TryGetValue(tabletTouchId, out var pointerId))
            {
                _usedPointerIds[pointerId] = false;
                _touchIdToPointerId.Remove(tabletTouchId);
            }
        }

        private void ClearAllState()
        {
            // Send canceled UP events for all active touches per MS docs
            if (_activeTouches.Count > 0)
            {
                var cancelInfos = new List<TouchInjector.POINTER_TOUCH_INFO>();
                foreach (var kvp in _activeTouches)
                {
                    var touch = kvp.Value;
                    var info = TouchInjector.POINTER_TOUCH_INFO.CreateCanceledUp(touch.PointerId, touch.LastUpdateX, touch.LastUpdateY);
                    cancelInfos.Add(info);
                }
                
                // Try to send cancel events (may fail, but we still need to clear state)
                if (cancelInfos.Count > 0)
                {
                    TouchInjector.Inject(cancelInfos.ToArray());
                }
            }

            // Clear all state
            foreach (var touchId in new List<int>(_activeTouches.Keys))
            {
                ReleasePointerId(touchId);
            }
            _activeTouches.Clear();
            _consecutiveFailures = 0;
        }

        /// <summary>
        /// Process a touch report and inject touch events
        /// </summary>
        public void ProcessTouchReport(TouchPoint?[] touchPoints, Matrix3x2 transformMatrix, Vector2 min, Vector2 max)
        {
            if (!TouchInjector.IsInitialized || touchPoints == null)
                return;

            lock (_lock)
            {
                var touchInfos = new List<TouchInjector.POINTER_TOUCH_INFO>();
                var currentTouchIds = new HashSet<int>();
                var pendingNewTouches = new List<(int tabletTouchId, uint pointerId, int x, int y)>();

                // Process active touches using the tablet's TouchID for stable tracking
                for (int i = 0; i < touchPoints.Length && i < MaxContacts; i++)
                {
                    var point = touchPoints[i];
                    if (point == null)
                        continue;

                    // Check if position is valid (not zero or negative)
                    if (point.Position.X <= 0 && point.Position.Y <= 0)
                        continue;

                    int tabletTouchId = point.TouchID;
                    currentTouchIds.Add(tabletTouchId);

                    // Transform the touch position from tablet to screen coordinates
                    var tabletPos = new Vector2(point.Position.X, point.Position.Y);
                    var screenPos = Vector2.Transform(tabletPos, transformMatrix);

                    // Apply Monitor Toggle multiplier and offset if the plugin is active
                    // This ensures touch follows pen when monitor is toggled
                    var monitorOffset = MonitorToggleDetector.GetOffset();
                    var monitorMultiplier = MonitorToggleDetector.GetMultiplier();
                    
                    // Apply multiplier (scales around display center)
                    if (monitorMultiplier != Vector2.One)
                    {
                        var displaySize = max - min;
                        
                        // Convert to unit coords (-1 to 1) relative to display
                        var unitPos = new Vector2(
                            (screenPos.X - min.X) / displaySize.X * 2 - 1,
                            (screenPos.Y - min.Y) / displaySize.Y * 2 - 1
                        );
                        
                        // Apply multiplier in unit space
                        unitPos *= monitorMultiplier;
                        
                        // Convert back to screen coordinates
                        screenPos = new Vector2(
                            (unitPos.X + 1) / 2 * displaySize.X + min.X,
                            (unitPos.Y + 1) / 2 * displaySize.Y + min.Y
                        );
                    }
                    
                    // Apply offset (shifts to different monitor)
                    screenPos += monitorOffset;

                    // Clamp to absolute screen bounds to prevent Error 87
                    int screenX = Math.Clamp((int)screenPos.X, _screenMinX, _screenMaxX);
                    int screenY = Math.Clamp((int)screenPos.Y, _screenMinY, _screenMaxY);

                    if (_activeTouches.TryGetValue(tabletTouchId, out var existingTouch))
                    {
                        // Touch update/move - use existing stable pointer ID
                        var info = TouchInjector.POINTER_TOUCH_INFO.CreateUpdate(existingTouch.PointerId, screenX, screenY);
                        touchInfos.Add(info);

                        // Store the LAST UPDATE position - critical for UP event
                        existingTouch.LastUpdateX = screenX;
                        existingTouch.LastUpdateY = screenY;
                    }
                    else
                    {
                        // New touch down - allocate a stable pointer ID
                        uint pointerId = AllocatePointerId(tabletTouchId);
                        var info = TouchInjector.POINTER_TOUCH_INFO.CreateDown(pointerId, screenX, screenY);
                        touchInfos.Add(info);

                        // Add to pending - will be added to active state ONLY after successful injection
                        pendingNewTouches.Add((tabletTouchId, pointerId, screenX, screenY));
                    }
                }

                // Handle lifted touches - generate UP events
                // CRITICAL: UP event must use the EXACT SAME position as the previous UPDATE frame
                var liftedTouchIds = new List<int>();
                foreach (var kvp in _activeTouches)
                {
                    if (!currentTouchIds.Contains(kvp.Key))
                    {
                        var touch = kvp.Value;
                        var info = TouchInjector.POINTER_TOUCH_INFO.CreateUp(touch.PointerId, touch.LastUpdateX, touch.LastUpdateY);
                        touchInfos.Add(info);
                        liftedTouchIds.Add(kvp.Key);
                    }
                }

                // Inject all touch events together in this frame
                if (touchInfos.Count > 0)
                {
                    var result = TouchInjector.Inject(touchInfos.ToArray());
                    
                    if (!result)
                    {
                        _consecutiveFailures++;
                        var error = Marshal.GetLastWin32Error();

                        if (_consecutiveFailures <= 3)
                        {
                            Log.Write("MultiTouchHandler", $"Injection failed (Error: {error}). Injecting {touchInfos.Count} points, {liftedTouchIds.Count} UP, {pendingNewTouches.Count} DOWN", LogLevel.Warning);
                            for (int i = 0; i < touchInfos.Count; i++)
                            {
                                var info = touchInfos[i];
                                var flags = info.pointerInfo.pointerFlags;
                                Log.Write("MultiTouchHandler", $"  [{i}] ID={info.pointerInfo.pointerId}, Pos=({info.pointerInfo.ptPixelLocation.x},{info.pointerInfo.ptPixelLocation.y}), Flags={flags}", LogLevel.Debug);
                            }
                        }

                        // Release pointer IDs for failed new touches
                        foreach (var pending in pendingNewTouches)
                        {
                            ReleasePointerId(pending.tabletTouchId);
                        }

                        if (_consecutiveFailures >= MaxFailuresBeforeReset)
                        {
                            Log.Write("MultiTouchHandler", "Persistent failure - sending cancel and clearing state", LogLevel.Warning);
                            ClearAllState();
                        }
                    }
                    else
                    {
                        _consecutiveFailures = 0;
                        
                        // NOW add new touches to active state (after successful injection)
                        foreach (var pending in pendingNewTouches)
                        {
                            _activeTouches[pending.tabletTouchId] = new TouchState
                            {
                                TabletTouchId = pending.tabletTouchId,
                                PointerId = pending.pointerId,
                                LastUpdateX = pending.x,
                                LastUpdateY = pending.y
                            };
                        }
                        
                        // Remove lifted touches from state
                        foreach (var touchId in liftedTouchIds)
                        {
                            ReleasePointerId(touchId);
                            _activeTouches.Remove(touchId);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Release all active touches
        /// </summary>
        public void ReleaseAllTouches()
        {
            lock (_lock)
            {
                if (_activeTouches.Count == 0)
                    return;

                var touchInfos = new List<TouchInjector.POINTER_TOUCH_INFO>();

                foreach (var kvp in _activeTouches)
                {
                    var touch = kvp.Value;
                    var info = TouchInjector.POINTER_TOUCH_INFO.CreateUp(touch.PointerId, touch.LastUpdateX, touch.LastUpdateY);
                    touchInfos.Add(info);
                    ReleasePointerId(kvp.Key);
                }

                if (touchInfos.Count > 0)
                {
                    TouchInjector.Inject(touchInfos.ToArray());
                }

                _activeTouches.Clear();
            }
        }

        public bool HasActiveTouches => _activeTouches.Count > 0;
        public int ActiveTouchCount => _activeTouches.Count;

        private class TouchState
        {
            public int TabletTouchId;
            public uint PointerId;
            public int LastUpdateX;
            public int LastUpdateY;
        }
    }
}
