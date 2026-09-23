using System.Collections;

namespace FileSharingModule.Models;

public class SwarmBitfield
{
    private readonly BitArray _bits;
    private readonly object _lock = new();

    public int TotalBlocks { get; }

    public SwarmBitfield(int totalBlocks)
    {
        TotalBlocks = totalBlocks;
        _bits = new BitArray(totalBlocks, false);
    }

    public SwarmBitfield(int totalBlocks, byte[] byteArray)
    {
        TotalBlocks = totalBlocks;
        _bits = new BitArray(byteArray);
        if (_bits.Length > totalBlocks)
        {
            _bits.Length = totalBlocks;
        }
    }

    public bool HasBlock(int blockIndex)
    {
        lock (_lock)
        {
            if (blockIndex < 0 || blockIndex >= TotalBlocks) return false;
            return _bits.Get(blockIndex);
        }
    }

    public void SetBlock(int blockIndex, bool value)
    {
        lock (_lock)
        {
            if (blockIndex < 0 || blockIndex >= TotalBlocks) return;
            _bits.Set(blockIndex, value);
        }
    }

    public bool IsComplete()
    {
        lock (_lock)
        {
            for (int i = 0; i < TotalBlocks; i++)
            {
                if (!_bits.Get(i)) return false;
            }
            return true;
        }
    }

    public int GetCompletedBlocksCount()
    {
        lock (_lock)
        {
            int count = 0;
            for (int i = 0; i < TotalBlocks; i++)
            {
                if (_bits.Get(i)) count++;
            }
            return count;
        }
    }

    public double GetProgressPercentage()
    {
        if (TotalBlocks == 0) return 0.0;
        return (double)GetCompletedBlocksCount() / TotalBlocks * 100.0;
    }

    /// <summary>
    /// Serializes bitfield into a compact byte array for BITFIELD (0x01) wire frames.
    /// </summary>
    public byte[] ToByteArray()
    {
        lock (_lock)
        {
            int byteCount = (TotalBlocks + 7) / 8;
            byte[] bytes = new byte[byteCount];
            _bits.CopyTo(bytes, 0);
            return bytes;
        }
    }
}