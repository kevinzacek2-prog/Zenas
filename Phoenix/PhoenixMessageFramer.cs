using System.Collections.Generic;
using System.Text;

namespace zenas.Phoenix
{
    public sealed class PhoenixMessageFramer
    {
        private readonly byte _terminator;
        private readonly List<byte> _acc = new List<byte>(16384);

        public PhoenixMessageFramer(byte terminator = 0x01)
        {
            _terminator = terminator;
        }

        public List<string> Push(byte[] buffer, int count)
        {
            var result = new List<string>();

            for (int i = 0; i < count; i++)
            {
                var b = buffer[i];

                if (b == _terminator)
                {
                    if (_acc.Count == 0)
                        continue;

                    var msg = Encoding.UTF8.GetString(_acc.ToArray());
                    _acc.Clear();
                    result.Add(msg);
                }
                else
                {
                    _acc.Add(b);
                }
            }

            return result;
        }
    }
}
