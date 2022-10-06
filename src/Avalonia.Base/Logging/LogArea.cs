namespace Avalonia.Logging
{
    /// <summary>
    /// Specifies the area in which a log event occurred.
    /// </summary>
    public static class LogArea
    {
        /// <summary>
        /// The log event comes from the property system.
        /// </summary>
        public const string Property = "Property";

        /// <summary>
        /// The log event comes from the binding system.
        /// </summary>
        public const string Binding = "Binding";

        /// <summary>
        /// The log event comes from the animations system.
        /// </summary>
        public const string Animations = "Animations";

        /// <summary>
        /// The log event comes from the visual system.
        /// </summary>
        public const string Visual = "Visual";

        /// <summary>
        /// The log event comes from the layout system.
        /// </summary>
        public const string Layout = "Layout";

        /// <summary>
        /// The log event comes from the control system.
        /// </summary>
        public const string Control = "Control";

        /// <summary>
        /// The log event comes from Win32 Platform.
        /// </summary>
        public const string Win32Platform = nameof(Win32Platform);
        
        /// <summary>
        /// The log event comes from X11 Platform.
        /// </summary>
        public const string X11Platform = nameof(X11Platform);

        /// <summary>
        /// The log event comes from Android Platform.
        /// </summary>
        public const string AndroidPlatform = nameof(AndroidPlatform);
        
        /// <summary>
        /// The log event comes from iOS Platform.
        /// </summary>
        public const string IOSPlatform = nameof(IOSPlatform);

        /// <summary>
        /// The log event comes from LinuxFramebuffer Platform
        /// </summary>
        public const string LinuxFramebufferPlatform = nameof(LinuxFramebufferPlatform);

        /// <summary>
        /// The log event comes from FreeDesktop Platform
        /// </summary>
        public const string FreeDesktopPlatform = nameof(FreeDesktopPlatform);

        /// <summary>
        /// The log event comes from macOS Platform
        /// </summary>
        public const string macOSPlatform = nameof(macOSPlatform);
    }
}
