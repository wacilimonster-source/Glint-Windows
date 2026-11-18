using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Glint.Windows.Platform;

/// <summary>解码微信输入法桥接写入的当前高亮。校验失败时不返回文字。</summary>
internal static class WeTypeSnapshot
{
    internal const int Size = 24 + TextCapacity * sizeof(char);
    internal const int TextCapacity = 120;
    private const int Sequence = 0;
    private const int Process = 4;
    private const int Thread = 8;
    private const int Tick = 12;
    internal const int Count = 16;
    internal const int AllowTick = 20;
    internal const int Text = 24;

    internal static bool TryDecode(ReadOnlySpan<byte> bytes, uint processId, uint threadId, out string text)
    {
        text = "";
        if (bytes.Length < Size) return false;
        int sequence = BinaryPrimitives.ReadInt32LittleEndian(bytes[Sequence..]);
        if ((sequence & 1) != 0) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[Process..]) != processId) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[Thread..]) != threadId) return false;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[Count..]);
        if (count is 0 or > TextCapacity - 1) return false;
        var characters = MemoryMarshal.Cast<byte, char>(bytes.Slice(Text, (int)count * sizeof(char)));
        foreach (char character in characters)
            if (char.IsControl(character)) return false;
        if (!characters.ToArray().Any(character => character is >= '\u3400' and <= '\u9fff')) return false;
        if (BinaryPrimitives.ReadInt32LittleEndian(bytes[Sequence..]) != sequence) return false;
        text = new string(characters);
        return true;
    }

    internal static uint ReadTick(ReadOnlySpan<byte> bytes) =>
        bytes.Length < Size ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(bytes[Tick..]);

    internal static void Check()
    {
        var bytes = new byte[Size];
        Write(bytes, 2, 10, 20, "你好");
        if (!TryDecode(bytes, 10, 20, out string text) || text != "你好")
            throw new InvalidOperationException("微信输入法快照未能还原高亮候选");
        Write(bytes, 3, 10, 20, "你好");
        if (TryDecode(bytes, 10, 20, out _)) throw new InvalidOperationException("未完成的微信输入法快照被接受");
        Write(bytes, 2, 11, 20, "你好");
        if (TryDecode(bytes, 10, 20, out _)) throw new InvalidOperationException("其他进程的微信输入法快照被接受");
        Write(bytes, 2, 10, 20, "你\n好");
        if (TryDecode(bytes, 10, 20, out _)) throw new InvalidOperationException("含控制字符的微信输入法快照被接受");
        Write(bytes, 2, 10, 20, "hello");
        if (TryDecode(bytes, 10, 20, out _)) throw new InvalidOperationException("不含中文的微信输入法快照被接受");
    }

    private static void Write(byte[] bytes, int sequence, uint processId, uint threadId, string text)
    {
        Array.Clear(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(Sequence), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(Process), processId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(Thread), threadId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(Count), (uint)text.Length);
        MemoryMarshal.AsBytes(text.AsSpan()).CopyTo(bytes.AsSpan(Text));
    }
}
