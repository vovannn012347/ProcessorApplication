using System.Buffers.Binary;

namespace FileTransferModule.Models;

public enum TorrentOpCode : byte
{
    Bitfield = 0x01,
    RequestSlice = 0x02,
    PieceSlice = 0x03,
    HaveBlock = 0x04,
    CancelSlice = 0x05
}

/// <summary>
/// Low-level binary wire protocol frame encoder and decoder for unmanaged WebRTC DataChannels.
/// </summary>
public static class TorrentWireFrame
{
    /// <summary>
    /// Encodes BITFIELD (0x01) payload byte array.
    /// Frame: [0x01][Bitfield bytes]
    /// </summary>
    public static byte[] EncodeBitfieldFrame(byte[] bitfieldBytes)
    {
        byte[] frame = new byte[1 + bitfieldBytes.Length];
        frame[0] = (byte)TorrentOpCode.Bitfield;
        Buffer.BlockCopy(bitfieldBytes, 0, frame, 1, bitfieldBytes.Length);
        return frame;
    }

    /// <summary>
    /// Encodes REQUEST_SLICE (0x02) request.
    /// Frame: [0x02][Int32 BlockIndex][Int32 SliceIndex]
    /// </summary>
    public static byte[] EncodeRequestSliceFrame(int blockIndex, int sliceIndex)
    {
        byte[] frame = new byte[1 + 4 + 4];
        frame[0] = (byte)TorrentOpCode.RequestSlice;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), blockIndex);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(5, 4), sliceIndex);
        return frame;
    }

    /// <summary>
    /// Encodes PIECE_SLICE (0x03) raw chunk delivery.
    /// Frame: [0x03][Int32 BlockIndex][Int32 SliceIndex][Raw Slice Bytes]
    /// </summary>
    public static byte[] EncodePieceSliceFrame(int blockIndex, int sliceIndex, byte[] rawSliceData)
    {
        byte[] frame = new byte[1 + 4 + 4 + rawSliceData.Length];
        frame[0] = (byte)TorrentOpCode.PieceSlice;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), blockIndex);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(5, 4), sliceIndex);
        Buffer.BlockCopy(rawSliceData, 0, frame, 9, rawSliceData.Length);
        return frame;
    }

    /// <summary>
    /// Encodes HAVE_BLOCK (0x04) broadcast notification.
    /// Frame: [0x04][Int32 BlockIndex]
    /// </summary>
    public static byte[] EncodeHaveBlockFrame(int blockIndex)
    {
        byte[] frame = new byte[1 + 4];
        frame[0] = (byte)TorrentOpCode.HaveBlock;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), blockIndex);
        return frame;
    }

    /// <summary>
    /// Encodes CANCEL_SLICE (0x05) cancellation request.
    /// Frame: [0x05][Int32 BlockIndex][Int32 SliceIndex]
    /// </summary>
    public static byte[] EncodeCancelSliceFrame(int blockIndex, int sliceIndex)
    {
        byte[] frame = new byte[1 + 4 + 4];
        frame[0] = (byte)TorrentOpCode.CancelSlice;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), blockIndex);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(5, 4), sliceIndex);
        return frame;
    }
}