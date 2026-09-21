using System;
using System.IO;
using System.Text;

namespace MerchantTerminal.Services;

/// <summary>
/// Mirrors everything written to the console into a daily log file under the
/// app-data folder (next to settings.json), so what the register saw is
/// readable afterwards — on the Windows build there is no console at all, and
/// on the bench the operator's terminal scrolls it away. Each line is stamped
/// with the wall clock; the console itself keeps the original text.
/// </summary>
public static class ConsoleLog
{
    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MerchantTerminal",
        "logs");

    public static string? CurrentFile { get; private set; }

    public static void Install()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            CurrentFile = Path.Combine(Directory, $"register-{DateTime.Now:yyyyMMdd}.log");
            var file = new StreamWriter(
                new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
            var tee = new TeeWriter(Console.Out, file);
            Console.SetOut(tee);
            Console.SetError(tee);
            Console.WriteLine($"[ConsoleLog] ---- register started {DateTime.Now:yyyy-MM-dd HH:mm:ss} — logging to {CurrentFile}");
            // Keep the previous week; anything older is noise.
            foreach (var old in System.IO.Directory.GetFiles(Directory, "register-*.log"))
            {
                if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-7)) File.Delete(old);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[ConsoleLog] file logging unavailable: {e.Message}");
        }
    }

    /// <summary>Console gets the text as written; the file gets a timestamp per line.</summary>
    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _console;
        private readonly TextWriter _file;
        private readonly object _gate = new();
        private readonly StringBuilder _line = new();

        public TeeWriter(TextWriter console, TextWriter file)
        {
            _console = console;
            _file = file;
        }

        public override Encoding Encoding => _console.Encoding;

        public override void Write(char value)
        {
            lock (_gate)
            {
                _console.Write(value);
                if (value == '\n')
                {
                    FlushLine();
                }
                else if (value != '\r')
                {
                    _line.Append(value);
                }
            }
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            lock (_gate)
            {
                _console.Write(value);
                foreach (var c in value)
                {
                    if (c == '\n') FlushLine();
                    else if (c != '\r') _line.Append(c);
                }
            }
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Write('\n');
        }

        private void FlushLine()
        {
            try
            {
                _file.Write(DateTime.Now.ToString("HH:mm:ss.fff "));
                _file.Write(_line.ToString());
                _file.Write('\n');
            }
            catch (IOException)
            {
                // Disk trouble must never take the register down.
            }
            _line.Clear();
        }

        public override void Flush()
        {
            _console.Flush();
            _file.Flush();
        }
    }
}
