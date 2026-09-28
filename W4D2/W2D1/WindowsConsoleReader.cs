using System.ComponentModel;
using System.Runtime.InteropServices;

namespace W2D1;

/// <summary>Обходит потерю не-ASCII символов при чтении консоли Windows в CP_UTF8.</summary>
// https://github.com/dotnet/runtime/issues/43295
internal sealed class WindowsConsoleReader : TextReader
{
    private readonly IntPtr _handle = GetStdHandle(-10); // STD_INPUT_HANDLE; не владеем дескриптором.
    private readonly char[] _buffer = new char[1024];
    private int _position;
    private int _length;
    private bool _endOfInput;

    public override int Peek() => EnsureBuffer() ? _buffer[_position] : -1;

    public override int Read() => EnsureBuffer() ? _buffer[_position++] : -1;

    // Базовый TextReader.ReadLine собирает строку через Read/Peek, включая длинные строки.
    // ReadConsoleW сохраняет обычное редактирование строки, вставку и обработку Ctrl+C.
    private bool EnsureBuffer()
    {
        if (_position < _length) return true;
        if (_endOfInput) return false;
        if (!ReadConsoleW(_handle, _buffer, (uint)_buffer.Length, out var read, IntPtr.Zero))
            throw new IOException("Не удалось прочитать ввод консоли Windows.", new Win32Exception(Marshal.GetLastWin32Error()));
        _position = 0;
        _length = (int)read;
        // Ctrl+Z в начале строки означает конец ввода, как у Console.ReadLine.
        if (_length == 0 || _buffer[0] == '\u001a')
        {
            _length = 0;
            _endOfInput = true;
            return false;
        }
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    // Windows возвращает UTF-16 символы прямо в char[]/.NET string без преобразования в ANSI.
    // Кодовая страница консоли остаётся UTF-8; сериализация и HTTP по-прежнему используют UTF-8.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadConsoleW(IntPtr input, [Out] char[] buffer, uint charsToRead,
        out uint charsRead, IntPtr inputControl);
}
