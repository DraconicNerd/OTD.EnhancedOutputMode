using System;
using System.Runtime.InteropServices;
using OpenTabletDriver.Plugin;

namespace OTD.EnhancedOutputMode.Touch
{
    /// <summary>
    /// Wrapper for Windows Touch Injection API (Windows 8+)
    /// </summary>
    public static class TouchInjector
    {
        private const string User32 = "user32.dll";

        #region Enums

        [Flags]
        public enum PointerFlags : uint
        {
            None = 0x00000000,
            New = 0x00000001,
            InRange = 0x00000002,
            InContact = 0x00000004,
            FirstButton = 0x00000010,
            SecondButton = 0x00000020,
            ThirdButton = 0x00000040,
            FourthButton = 0x00000080,
            FifthButton = 0x00000100,
            Primary = 0x00002000,
            Confidence = 0x00004000,
            Canceled = 0x00008000,
            Down = 0x00010000,
            Update = 0x00020000,
            Up = 0x00040000,
            Wheel = 0x00080000,
            HWheel = 0x00100000,
            CaptureChanged = 0x00200000,
            HasTransform = 0x00400000
        }

        public enum PointerInputType : uint
        {
            Pointer = 0x00000001,
            Touch = 0x00000002,
            Pen = 0x00000003,
            Mouse = 0x00000004,
            TouchPad = 0x00000005
        }

        [Flags]
        public enum TouchMask : uint
        {
            None = 0x00000000,
            ContactArea = 0x00000001,
            Orientation = 0x00000002,
            Pressure = 0x00000004
        }

        #endregion

        #region Structures

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINTER_INFO
        {
            public PointerInputType pointerType;
            public uint pointerId;
            public uint frameId;
            public PointerFlags pointerFlags;
            public IntPtr sourceDevice;
            public IntPtr hwndTarget;
            public POINT ptPixelLocation;
            public POINT ptHimetricLocation;
            public POINT ptPixelLocationRaw;
            public POINT ptHimetricLocationRaw;
            public uint dwTime;
            public uint historyCount;
            public int inputData;
            public uint dwKeyStates;
            public ulong performanceCount;
            public uint ButtonChangeType; // NOTE: must be uint, not int
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINTER_TOUCH_INFO
        {
            public POINTER_INFO pointerInfo;
            public uint touchFlags;
            public uint touchMask;
            public RECT rcContact;
            public RECT rcContactRaw;
            public uint orientation;
            public uint pressure;

            /// <summary>
            /// Creates a touch info for a touch point at the specified screen coordinates.
            /// PointerId should be in range 1-10 for touch injection (0 is reserved).
            /// </summary>
            public static POINTER_TOUCH_INFO Create(uint pointerId, int x, int y, PointerFlags flags)
            {
                // Contact radius of 1 creates a small touch point
                int contactRadius = 1;
                int left = Math.Max(0, x - contactRadius);
                int top = Math.Max(0, y - contactRadius);
                int right = x + contactRadius;
                int bottom = y + contactRadius;

                return new POINTER_TOUCH_INFO
                {
                    pointerInfo = new POINTER_INFO
                    {
                        pointerType = PointerInputType.Touch,
                        pointerId = pointerId,
                        pointerFlags = flags,
                        ptPixelLocation = new POINT { x = x, y = y }
                    },
                    touchFlags = 0,
                    touchMask = (uint)(TouchMask.ContactArea | TouchMask.Orientation | TouchMask.Pressure),
                    rcContact = new RECT
                    {
                        left = left,
                        top = top,
                        right = right,
                        bottom = bottom
                    },
                    orientation = 90,
                    pressure = 512
                };
            }

            /// <summary>
            /// Creates a touch down event
            /// </summary>
            public static POINTER_TOUCH_INFO CreateDown(uint pointerId, int x, int y)
            {
                return Create(pointerId, x, y, 
                    PointerFlags.Down | PointerFlags.InRange | PointerFlags.InContact);
            }

            /// <summary>
            /// Creates a touch move/update event
            /// </summary>
            public static POINTER_TOUCH_INFO CreateUpdate(uint pointerId, int x, int y)
            {
                return Create(pointerId, x, y,
                    PointerFlags.Update | PointerFlags.InRange | PointerFlags.InContact);
            }

            /// <summary>
            /// Creates a touch up event
            /// Per MS docs: "Cancel individual contacts by setting POINTER_FLAG_CANCELED with POINTER_FLAG_UP"
            /// </summary>
            public static POINTER_TOUCH_INFO CreateUp(uint pointerId, int x, int y)
            {
                // Individual contact release requires CANCELED flag per MS documentation
                return Create(pointerId, x, y, PointerFlags.Up | PointerFlags.Canceled);
            }

            /// <summary>
            /// Creates a canceled touch up event (for cleanup/error recovery)
            /// Per MS docs: "Cancel individual contacts by setting POINTER_FLAG_CANCELED with POINTER_FLAG_UP"
            /// </summary>
            public static POINTER_TOUCH_INFO CreateCanceledUp(uint pointerId, int x, int y)
            {
                return Create(pointerId, x, y, PointerFlags.Up | PointerFlags.Canceled);
            }
        }

        #endregion

        #region P/Invoke

        [DllImport(User32, SetLastError = true)]
        public static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);

        [DllImport(User32, SetLastError = true)]
        public static extern bool InjectTouchInput(uint count, [MarshalAs(UnmanagedType.LPArray), In] POINTER_TOUCH_INFO[] contacts);

        #endregion

        #region Constants

        public const uint TOUCH_FEEDBACK_DEFAULT = 0x1;
        public const uint TOUCH_FEEDBACK_INDIRECT = 0x2;
        public const uint TOUCH_FEEDBACK_NONE = 0x3;

        #endregion

        #region Initialization

        private static bool _initialized = false;
        private static readonly object _initLock = new();

        public static bool Initialize(uint maxContacts = 10)
        {
            lock (_initLock)
            {
                if (_initialized)
                    return true;

                try
                {
                    _initialized = InitializeTouchInjection(maxContacts, TOUCH_FEEDBACK_DEFAULT);
                    if (!_initialized)
                    {
                        var error = Marshal.GetLastWin32Error();
                        Log.Write("TouchInjector", 
                            $"Failed to initialize touch injection. Error: {error}", 
                            LogLevel.Error);
                    }
                    else
                    {
                        Log.Write("TouchInjector", 
                            $"Touch injection initialized with max {maxContacts} contacts");
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("TouchInjector", 
                        $"Exception initializing touch injection: {ex.Message}", 
                        LogLevel.Error);
                    _initialized = false;
                }

                return _initialized;
            }
        }

        public static bool IsInitialized => _initialized;

        public static bool Inject(POINTER_TOUCH_INFO[] contacts)
        {
            if (!_initialized || contacts == null || contacts.Length == 0)
                return false;

            try
            {
                return InjectTouchInput((uint)contacts.Length, contacts);
            }
            catch (Exception ex)
            {
                Log.Write("TouchInjector", 
                    $"Exception injecting touch: {ex.Message}", 
                    LogLevel.Error);
                return false;
            }
        }

        #endregion
    }
}
