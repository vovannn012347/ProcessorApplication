
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using FileTransferModule.Models;

namespace FileTransferModule.Services;

public class ParsedWireFrame
{
    public TorrentOpCode OpCode { get; set; }
    public int BlockIndex { get; set; }
    public int SliceIndex { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public bool IsValid { get; set; }
}

/// <summary>
/// Encapsulates binary frame encoding and decoding over unmanaged WebRTC DataChannels.
/// </summary>
public interface ITorrentWireHandler
{
    /// <summary>
    /// Parses incoming raw WebRTC binary frame bytes.
    /// </summary>
    ParsedWireFrame ParseFrame(ReadOnlySpan<byte> rawFrameBytes);

    /// <summary>
    /// Encodes a BITFIELD (0x01) frame.
    /// </summary>
    byte[] BuildBitfieldFrame(SwarmBitfield bitfield);

    /// <summary>
    /// Encodes a REQUEST_SLICE (0x02) frame.
    /// </summary>
    byte[] BuildRequestSliceFrame(int blockIndex, int sliceIndex);

    /// <summary>
    /// Encodes a PIECE_SLICE (0x03) frame carrying raw slice data.
    /// </summary>
    byte[] BuildPieceSliceFrame(int blockIndex, int sliceIndex, byte[] rawSliceData);

    /// <summary>
    /// Encodes a HAVE_BLOCK (0x04) broadcast frame.
    /// </summary>
    byte[] BuildHaveBlockFrame(int blockIndex);

    /// <summary>
    /// Encodes a CANCEL_SLICE (0x05) cancellation frame.
    /// </summary>
    byte[] BuildCancelSliceFrame(int blockIndex, int sliceIndex);
}


/// <summary>
/// Binary wire protocol processor utilizing Span&lt;byte&gt; and BinaryPrimitives
/// for zero-allocation frame serialization and deserialization over unmanaged WebRTC channels.
/// </summary>
public class TorrentWireHandler : ITorrentWireHandler
{
    /// <summary>
    /// Decodes incoming binary frames.
    /// Opcodes:
    ///   0x01: BITFIELD      -> [0x01][Payload bytes]
    ///   0x02: REQUEST_SLICE -> [0x02][Int32 BlockIndex][Int32 SliceIndex]
    ///   0x03: PIECE_SLICE   -> [0x03][Int32 BlockIndex][Int32 SliceIndex][Slice Bytes]
    ///   0x04: HAVE_BLOCK    -> [0x04][Int32 BlockIndex]
    ///   0x05: CANCEL_SLICE  -> [0x05][Int32 BlockIndex][Int32 SliceIndex]
    /// </summary>
    public ParsedWireFrame ParseFrame(ReadOnlySpan<byte> rawFrameBytes)
    {
        var result = new ParsedWireFrame { IsValid = false };

        if (rawFrameBytes.IsEmpty)
            return result;

        byte opCodeByte = rawFrameBytes[0];
        if (!Enum.IsDefined(typeof(TorrentOpCode), opCodeByte))
            return result;

        result.OpCode = (TorrentOpCode)opCodeByte;
        ReadOnlySpan<byte> payloadSpan = rawFrameBytes.Slice(1);

        switch (result.OpCode)
        {
            case TorrentOpCode.Bitfield:
                result.Payload = payloadSpan.ToArray();
                result.IsValid = true;
                break;

            case TorrentOpCode.RequestSlice:
            case TorrentOpCode.CancelSlice:
                if (payloadSpan.Length >= 8)
                {
                    result.BlockIndex = BinaryPrimitives.ReadInt32BigEndian(payloadSpan.Slice(0, 4));
                    result.SliceIndex = BinaryPrimitives.ReadInt32BigEndian(payloadSpan.Slice(4, 4));
                    result.IsValid = true;
                }
                break;

            case TorrentOpCode.PieceSlice:
                if (payloadSpan.Length >= 8)
                {
                    result.BlockIndex = BinaryPrimitives.ReadInt32BigEndian(payloadSpan.Slice(0, 4));
                    result.SliceIndex = BinaryPrimitives.ReadInt32BigEndian(payloadSpan.Slice(4, 4));
                    result.Payload = payloadSpan.Slice(8).ToArray();
                    result.IsValid = true;
                }
                break;

            case TorrentOpCode.HaveBlock:
                if (payloadSpan.Length >= 4)
                {
                    result.BlockIndex = BinaryPrimitives.ReadInt32BigEndian(payloadSpan.Slice(0, 4));
                    result.IsValid = true;
                }
                break;
        }

        return result;
    }

    public byte[] BuildBitfieldFrame(SwarmBitfield bitfield)
    {
        byte[] bitfieldBytes = bitfield?.ToByteArray() ?? Array.Empty<byte>();
        return TorrentWireFrame.EncodeBitfieldFrame(bitfieldBytes);
    }

    public byte[] BuildRequestSliceFrame(int blockIndex, int sliceIndex)
    {
        return TorrentWireFrame.EncodeRequestSliceFrame(blockIndex, sliceIndex);
    }

    public byte[] BuildPieceSliceFrame(int blockIndex, int sliceIndex, byte[] rawSliceData)
    {
        return TorrentWireFrame.EncodePieceSliceFrame(blockIndex, sliceIndex, rawSliceData);
    }

    public byte[] BuildHaveBlockFrame(int blockIndex)
    {
        return TorrentWireFrame.EncodeHaveBlockFrame(blockIndex);
    }

    public byte[] BuildCancelSliceFrame(int blockIndex, int sliceIndex)
    {
        return TorrentWireFrame.EncodeCancelSliceFrame(blockIndex, sliceIndex);
    }
}