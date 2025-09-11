using System;
using System.IO;
using System.Text;

namespace OpenLibraryServer
{
    public sealed class FileLogger : IDisposable
    {
        private readonly object _lock = new object();
        private readonly string _dir;
        private string _currentPath;
        private StreamWriter _writer;

        public FileLogger(string directory = "logs")
        {
            _dir = directory;
            Directory.CreateDirectory(_dir);
            _currentPath = Path.Combine(_dir, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".log");
            _writer = OpenWriter(_currentPath);
        }

        private static StreamWriter OpenWriter(string path)
        {
            var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            return new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
        }

        public void Log(string line)
        {
            lock (_lock)
            {
                var path = Path.Combine(_dir, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".log");
                if (!string.Equals(path, _currentPath, StringComparison.Ordinal))
                {
                    _writer.Dispose();
                    _currentPath = path;
                    _writer = OpenWriter(_currentPath);
                }
                _writer.WriteLine(line);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _writer?.Dispose();
            }
        }
    }
}
