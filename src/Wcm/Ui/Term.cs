using System.Runtime.InteropServices;
using System.Text;

namespace Wcm.Ui;

internal static class Style
{
    public const string Reset = "\e[0m";
    public const string Bold = "\e[1m";
    public const string Dim = "\e[2m";
    public const string Reverse = "\e[7m";
    public const string Red = "\e[31m";
    public const string Green = "\e[32m";
    public const string Yellow = "\e[33m";
    public const string Blue = "\e[34m";
    public const string Magenta = "\e[35m";
    public const string Cyan = "\e[36m";
    public const string Gray = "\e[90m";
    public const string Header = "\e[1;37;44m";
    public const string Footer = "\e[30;47m";
}

/// <summary>Bare-bones full-screen terminal: alternate buffer, whole-frame redraws, ANSI styles.</summary>
internal static partial class Term
{
    private static int _lastW, _lastH;

    public static int Width => Math.Max(40, SafeGet(() => Console.WindowWidth, 120));
    public static int Height => Math.Max(10, SafeGet(() => Console.WindowHeight, 30));

    public static void Enter()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        EnableVirtualTerminal();
        Console.TreatControlCAsInput = true;
        Console.Write("\e[?1049h\e[?25l\e[2J");
    }

    public static void Leave()
    {
        Console.Write("\e[0m\e[?25h\e[?1049l");
        Console.TreatControlCAsInput = false;
    }

    public static void Draw(Frame frame)
    {
        var sb = new StringBuilder();
        if (frame.Width != _lastW || frame.Height != _lastH)
        {
            sb.Append("\e[2J");
            _lastW = frame.Width;
            _lastH = frame.Height;
        }
        for (var row = 0; row < frame.Height; row++)
        {
            sb.Append("\e[").Append(row + 1).Append(";1H");
            if (row < frame.Lines.Count) sb.Append(frame.Lines[row]);
            sb.Append(Style.Reset).Append("\e[K");
        }
        if (frame.Cursor is { } c) sb.Append($"\e[{c.Row + 1};{c.Col + 1}H\e[?25h");
        else sb.Append("\e[?25l");
        Console.Write(sb.ToString());
    }

    /// <summary>Blocks until a key is pressed, redrawing on terminal resize.</summary>
    public static ConsoleKeyInfo ReadKey(Func<Frame>? redraw = null, int pollMs = 100)
    {
        while (!Console.KeyAvailable)
        {
            if (redraw is not null && (Width != _lastW || Height != _lastH)) Draw(redraw());
            Thread.Sleep(pollMs);
        }
        return Normalize(Console.ReadKey(intercept: true));
    }

    public static ConsoleKeyInfo? TryReadKey() => Console.KeyAvailable ? Normalize(Console.ReadKey(intercept: true)) : null;

    // Some hosts (ConPTY, ssh) deliver only the char without a virtual key code
    private static ConsoleKeyInfo Normalize(ConsoleKeyInfo k)
    {
        if (k.Key != 0) return k;
        var c = k.KeyChar;
        ConsoleKey key = c switch
        {
            >= 'a' and <= 'z' => (ConsoleKey)char.ToUpperInvariant(c),
            >= 'A' and <= 'Z' => (ConsoleKey)c,
            >= '0' and <= '9' => (ConsoleKey)c,
            ' ' => ConsoleKey.Spacebar,
            '\r' or '\n' => ConsoleKey.Enter,
            '\b' or '\x7f' => ConsoleKey.Backspace,
            '\x1b' => ConsoleKey.Escape,
            '\t' => ConsoleKey.Tab,
            _ => 0
        };
        if (key == 0) return k;
        var shift = k.Modifiers.HasFlag(ConsoleModifiers.Shift) || c is >= 'A' and <= 'Z';
        return new ConsoleKeyInfo(c, key, shift, k.Modifiers.HasFlag(ConsoleModifiers.Alt), k.Modifiers.HasFlag(ConsoleModifiers.Control));
    }

    private static T SafeGet<T>(Func<T> f, T fallback)
    {
        try { return f(); } catch { return fallback; }
    }

    private const int STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private static void EnableVirtualTerminal()
    {
        var h = GetStdHandle(STD_OUTPUT_HANDLE);
        if (GetConsoleMode(h, out var mode)) SetConsoleMode(h, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
    }
}

internal sealed class Frame
{
    public Frame()
    {
        Width = Term.Width;
        Height = Term.Height;
    }

    public int Width { get; }
    public int Height { get; }
    public List<string> Lines { get; } = [];
    public (int Row, int Col)? Cursor { get; set; }

    public int Remaining => Height - Lines.Count;

    public Frame Add(string text, string style = "") => Add(new Line(Width).Text(text, style).Fill(style));

    public Frame Add(Line line)
    {
        if (Lines.Count < Height) Lines.Add(line.ToString());
        return this;
    }

    public Frame Blank() => Add("");

    /// <summary>Pads with empty lines so the footer lands on the bottom rows.</summary>
    public Frame FillTo(int rowsLeftForFooter)
    {
        while (Lines.Count < Height - rowsLeftForFooter) Lines.Add("");
        return this;
    }
}

/// <summary>A single styled row, truncated to the screen width.</summary>
internal sealed class Line(int width, string baseStyle = "")
{
    private readonly StringBuilder _sb = new();
    private int _len;
    private readonly int _max = width - 1;

    public int Length => _len;

    public Line Text(string text, string style = "")
    {
        if (_len >= _max || text.Length == 0) return this;
        var room = _max - _len;
        if (text.Length > room) text = room > 1 ? text[..(room - 1)] + "…" : text[..room];
        var st = baseStyle + style;
        if (st.Length > 0) _sb.Append(st);
        _sb.Append(text);
        if (st.Length > 0) _sb.Append(Style.Reset);
        _len += text.Length;
        return this;
    }

    public Line Pad(int column, string style = "")
    {
        if (column > _len) Text(new string(' ', Math.Min(column, _max) - _len), style);
        return this;
    }

    /// <summary>Extends the current style to the end of the row (for selection bars).</summary>
    public Line Fill(string style = "") => style.Length == 0 && baseStyle.Length == 0 ? this : Pad(_max, style);

    public Line Right(string text, string style = "")
    {
        var col = _max - text.Length;
        if (col > _len) Pad(col);
        return Text(text, style);
    }

    public override string ToString() => _sb.ToString();
}

internal static class Fmt
{
    public static string Bar(double fraction, int width)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        var full = (int)Math.Round(fraction * width);
        return new string('█', full) + new string('░', width - full);
    }

    public static string MiddleTrim(string s, int max)
    {
        if (max <= 3 || s.Length <= max) return s.Length <= max ? s : s[..Math.Max(0, max)];
        var head = max / 3;
        var tail = max - head - 1;
        return s[..head] + "…" + s[^tail..];
    }

    public static string Size(long bytes) => Model.ByteSize.Format(bytes).PadLeft(9);
}
