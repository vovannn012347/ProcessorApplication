namespace Common.Interfaces;
public interface IEncryptable
{
    void EncryptData(byte[] key);

    void DecryptData(byte[] key);
}