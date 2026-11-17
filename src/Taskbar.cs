using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace PropnightRevival
{
    // The unread messages count on the launcher's taskbar button (a red badge with the number, as Discord), Windows 7 and up.
    static class Taskbar
    {
        [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ITaskbarList3
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
            void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
            void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
            void SetProgressState(IntPtr hwnd, int flags);
            void RegisterTab(IntPtr tab, IntPtr main);
            void UnregisterTab(IntPtr tab);
            void SetTabOrder(IntPtr tab, IntPtr insertBefore);
            void SetTabActive(IntPtr tab, IntPtr main, uint reserved);
            void ThumbBarAddButtons(IntPtr hwnd, uint count, IntPtr buttons);
            void ThumbBarUpdateButtons(IntPtr hwnd, uint count, IntPtr buttons);
            void ThumbBarSetImageList(IntPtr hwnd, IntPtr imageList);
            void SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string description);
        }

        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
        class TaskbarList { }

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr icon);

        static ITaskbarList3 list;
        static IntPtr shown = IntPtr.Zero;
        static int count = -1;

        /// <summary>The badge with n (none at 0); drawn again only when n changes.</summary>
        public static void SetCount(IntPtr window, int n)
        {
            n = Math.Max(0, n);
            if (n == count) return;
            count = n;
            try
            {
                if (list == null)
                {
                    list = (ITaskbarList3)new TaskbarList();
                    list.HrInit();
                }
                var icon = n > 0 ? Badge(n > 9 ? "9+" : n.ToString()) : IntPtr.Zero;
                list.SetOverlayIcon(window, icon, n > 0 ? $"{n} unread message{(n == 1 ? "" : "s")}" : "");
                if (shown != IntPtr.Zero) DestroyIcon(shown);
                shown = icon;
            }
            catch (Exception ex) { Log.Write("taskbar badge: " + ex.Message); }
        }

        // a red disc with the number in white, 32 px (Windows shrinks it to the corner of the button)
        static IntPtr Badge(string text)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    using (var red = new SolidBrush(Color.FromArgb(239, 69, 80))) g.FillEllipse(red, 0, 0, 31, 31);
                    using (var font = new Font("Segoe UI", text.Length > 1 ? 13f : 17f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var white = new SolidBrush(Color.White))
                    using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(text, font, white, new RectangleF(0, 0, 32, 33), fmt);
                }
                return bmp.GetHicon();
            }
        }
    }
}
