namespace FileSharingModule.Models.Torrent;

public class TorrentBlockDescriptor
{
    public int BlockIndex { get; set; }
    public long ByteOffset { get; set; }
    public int BlockLength { get; set; }
    public int SliceCount { get; set; }
    public string ExpectedSha256Hash { get; set; } = string.Empty;
}