namespace RadiologyPlus.Common.Encryption;

public interface IEncryptionService
{
    byte[] Encrypt(string plaintext);
    string Decrypt(byte[] ciphertext);
}
