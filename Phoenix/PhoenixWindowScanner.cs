using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace zenas.Phoenix
{
    public sealed class PhoenixWindowScanner
    {
        private readonly string _windowMarker;

        public PhoenixWindowScanner(string windowMarker)
        {
            _windowMarker = windowMarker;
        }

        public SortedDictionary<int, string> ScanPortToName()
        {
            var result = new SortedDictionary<int, string>();

            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd))
                    return true;

                var sb = new StringBuilder(2048);
                GetWindowText(hWnd, sb, sb.Capacity);
                var title = sb.ToString();

                if (string.IsNullOrWhiteSpace(title))
                    return true;

                if (title.IndexOf(_windowMarker, StringComparison.OrdinalIgnoreCase) < 0)
                    return true;

                var name = ExtractName(title);
                var ports = ExtractPorts(title);

                if (!string.IsNullOrWhiteSpace(name) && ports.Count > 0)
                {
                    foreach (var p in ports)
                        result[p] = name!;
                }

                return true;
            }, IntPtr.Zero);

            return result;
        }

        private static string? ExtractName(string title)
        {
            // příklad: [Lv. 90(+10) HollyM1] -> HollyM1
            var m = Regex.Match(title, @"\[(.*?)\]");
            if (!m.Success) return null;

            var inside = m.Groups[1].Value.Trim();
            var parts = inside.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;

            return parts[^1];
        }

        private List<int> ExtractPorts(string title)
        {
            var idx = title.IndexOf(_windowMarker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return new List<int>();

            var after = title.Substring(idx);
            var ports = new List<int>();

            foreach (Match m in Regex.Matches(after, @"\b\d{2,6}\b"))
            {
                if (int.TryParse(m.Value, out int p) && p >= 1 && p <= 99999)
                    ports.Add(p);
            }

            // distinct + order
            var set = new HashSet<int>(ports);
            var list = new List<int>(set);
            list.Sort();
            return list;
        }

        // ===== WinAPI =====
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
    }
}
