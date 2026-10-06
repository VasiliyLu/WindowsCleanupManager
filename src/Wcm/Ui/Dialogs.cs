namespace Wcm.Ui;

/// <summary>Modal prompts drawn over the bottom rows of the current screen.</summary>
internal static class Dialogs
{
    private static Frame Overlay(Func<Frame> background, IReadOnlyList<Line> rows)
    {
        var f = background();
        f.FillTo(0);
        var start = Math.Max(0, f.Height - rows.Count);
        for (var i = 0; i < rows.Count && start + i < f.Lines.Count; i++)
            f.Lines[start + i] = rows[i].ToString();
        return f;
    }

    private static Line Row(int width, string text = "", string style = Style.Footer) => new Line(width, style).Text(" " + text).Fill();

    public static void Message(Func<Frame> background, string title, params string[] lines)
    {
        var w = Term.Width;
        var rows = new List<Line> { Row(w, title, "\e[1;37;45m") };
        rows.AddRange(lines.Select(l => Row(w, l)));
        rows.Add(Row(w, "любая клавиша — продолжить", "\e[2;30;47m"));
        Term.Draw(Overlay(background, rows));
        Term.ReadKey();
    }

    public static bool Confirm(Func<Frame> background, string question, params string[] details)
    {
        var w = Term.Width;
        var rows = new List<Line> { Row(w, question, "\e[1;37;41m") };
        rows.AddRange(details.Select(d => Row(w, d)));
        rows.Add(Row(w, "y — да,  n / Esc — нет", "\e[2;30;47m"));
        Term.Draw(Overlay(background, rows));
        while (true)
        {
            var k = Term.ReadKey();
            if (k.Key == ConsoleKey.Y) return true;
            if (k.Key is ConsoleKey.N or ConsoleKey.Escape) return false;
        }
    }

    /// <summary>Returns index of the chosen option or -1.</summary>
    public static int Choose(Func<Frame> background, string title, IReadOnlyList<string> options, int selected = 0)
    {
        while (true)
        {
            var w = Term.Width;
            var rows = new List<Line> { Row(w, title, "\e[1;37;45m") };
            for (var i = 0; i < options.Count; i++)
                rows.Add(Row(w, $"{i + 1}. {options[i]}", i == selected ? "\e[30;46m" : Style.Footer));
            rows.Add(Row(w, "↑↓ / цифра — выбор,  Enter — ок,  Esc — отмена", "\e[2;30;47m"));
            Term.Draw(Overlay(background, rows));

            var k = Term.ReadKey();
            switch (k.Key)
            {
                case ConsoleKey.UpArrow: selected = (selected - 1 + options.Count) % options.Count; break;
                case ConsoleKey.DownArrow: selected = (selected + 1) % options.Count; break;
                case ConsoleKey.Enter: return selected;
                case ConsoleKey.Escape: return -1;
                default:
                    if (k.KeyChar is >= '1' and <= '9' && k.KeyChar - '1' < options.Count) return k.KeyChar - '1';
                    break;
            }
        }
    }

    /// <summary>Single-line editor. Returns null on Esc.</summary>
    public static string? Prompt(Func<Frame> background, string title, string initial = "", string? hint = null)
    {
        var text = new System.Text.StringBuilder(initial);
        var pos = text.Length;
        while (true)
        {
            var w = Term.Width;
            var field = w - 4;
            // Scroll the visible window so the cursor stays on screen
            var offset = Math.Max(0, pos - field + 1);
            var visible = text.ToString().Substring(offset, Math.Min(field, text.Length - offset));

            var rows = new List<Line>
            {
                Row(w, title, "\e[1;37;45m"),
                new Line(w, "\e[30;46m").Text(" " + visible).Fill(),
                Row(w, hint ?? "Enter — ок,  Esc — отмена", "\e[2;30;47m")
            };
            var f = Overlay(background, rows);
            f.Cursor = (f.Height - 2, 1 + pos - offset);
            Term.Draw(f);

            var k = Term.ReadKey();
            switch (k.Key)
            {
                case ConsoleKey.Enter: return text.ToString();
                case ConsoleKey.Escape: return null;
                case ConsoleKey.LeftArrow: pos = Math.Max(0, pos - 1); break;
                case ConsoleKey.RightArrow: pos = Math.Min(text.Length, pos + 1); break;
                case ConsoleKey.Home: pos = 0; break;
                case ConsoleKey.End: pos = text.Length; break;
                case ConsoleKey.Backspace:
                    if (pos > 0) { text.Remove(pos - 1, 1); pos--; }
                    break;
                case ConsoleKey.Delete:
                    if (pos < text.Length) text.Remove(pos, 1);
                    break;
                default:
                    if (!char.IsControl(k.KeyChar)) { text.Insert(pos, k.KeyChar); pos++; }
                    break;
            }
        }
    }
}
