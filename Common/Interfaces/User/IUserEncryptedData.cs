namespace ProcessorApplication.Models.User
{
    public interface IUserEncryptedData
    {
        public DateTime CreateDateTime { get; set; }
        public string EncryptionHash { get; set; }
        public bool IsEncrypted { get; set; }
    }
}