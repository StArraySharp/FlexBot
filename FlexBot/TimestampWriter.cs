using System.Text;

namespace FlexBot
{
    class TimestampWriter(TextWriter inner) : TextWriter
    {
        private bool _atLineStart = true;
        private readonly StringBuilder _line = new();

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            if (_atLineStart)
            {
                var prefix = $"[{DateTime.Now:HH:mm:ss.fff}] ";
                inner.Write(prefix);
                _line.Append(prefix);
                _atLineStart = false;
            }
            inner.Write(value);
            if (value == '\n')
            {
                var line = _line.ToString().TrimEnd('\r', '\n');
                LogStore.AppendLine(line);
                _line.Clear();
                _atLineStart = true;
            }
            else
            {
                _line.Append(value);
            }
        }
    }
}
