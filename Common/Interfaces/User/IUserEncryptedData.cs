namespace ProcessorApplication.Models.User
{
    public interface IUserEncryptedData
    {
        public DateTime CreateDateTime { get; set; }
        public string EncryptionKey { get; set; }
        public bool IsEncrypted { get; set; }
    }
}