using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InfraDroneDesktop.Services;

public class DeviceLicense
{
    public string DeviceId { get; set; } = "";
    public string LicenseKey { get; set; } = "";
    public string Operator { get; set; } = "";
    public string IssuedTo { get; set; } = "";
    public DateTime IssuedDate { get; set; }
    public DateTime ExpiryDate { get; set; }
    public bool IsValid => DateTime.UtcNow < ExpiryDate;
}

public class EncryptionService
{
    private byte[]? _key;
    private readonly string _keyPath;
    private DeviceLicense? _license;

    public bool IsEncryptionEnabled { get; private set; } = false;
    public DeviceLicense? License => _license;
    public string Status { get; private set; } = "Encryption not configured";

    public EncryptionService()
    {
        _keyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InfraDrone", "license.json");
    }

    // Generate a new encryption key for a device
    public static string GenerateKey()
    {
        var key = new byte[32]; // 256-bit
        RandomNumberGenerator.Fill(key);
        return Convert.ToBase64String(key);
    }

    // Generate a unique device ID based on machine fingerprint
    public static string GetDeviceId()
    {
        var hostname = Environment.MachineName;
        var user = Environment.UserName;
        var fingerprint = $"{hostname}:{user}:DAMbv-InfraDrone";
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(fingerprint));
        return Convert.ToHexString(hash)[..16].ToUpper();
    }

    // Issue a new license (called by DAMbv when selling to new operator)
    public static DeviceLicense IssueLicense(string operatorName, string issuedTo, int validDays = 365)
    {
        return new DeviceLicense
        {
            DeviceId = GetDeviceId(),
            LicenseKey = GenerateKey(),
            Operator = operatorName,
            IssuedTo = issuedTo,
            IssuedDate = DateTime.UtcNow,
            ExpiryDate = DateTime.UtcNow.AddDays(validDays)
        };
    }

    // Load license from disk
    public bool LoadLicense()
    {
        try
        {
            if (!File.Exists(_keyPath))
            {
                Status = "No license file found";
                return false;
            }
            var json = File.ReadAllText(_keyPath);
            _license = JsonSerializer.Deserialize<DeviceLicense>(json);
            if (_license == null || !_license.IsValid)
            {
                Status = _license == null ? "Invalid license" : "License expired";
                return false;
            }
            _key = Convert.FromBase64String(_license.LicenseKey);
            IsEncryptionEnabled = true;
            Status = $"Licensed to {_license.IssuedTo} — expires {_license.ExpiryDate:yyyy-MM-dd}";
            return true;
        }
        catch (Exception ex)
        {
            Status = $"License error: {ex.Message}";
            return false;
        }
    }

    // Save license to disk
    public void SaveLicense(DeviceLicense license)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
        var json = JsonSerializer.Serialize(license, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_keyPath, json);
        _license = license;
        _key = Convert.FromBase64String(license.LicenseKey);
        IsEncryptionEnabled = true;
        Status = $"Licensed to {license.IssuedTo} — expires {license.ExpiryDate:yyyy-MM-dd}";
    }

    // Encrypt a MAVLink packet
    public byte[] Encrypt(byte[] plaintext)
    {
        if (_key == null || !IsEncryptionEnabled) return plaintext;
        using var aes = new AesGcm(_key, AesGcm.TagByteSizes.MaxSize);
        var nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        // Format: [4-byte length][12-byte nonce][16-byte tag][ciphertext]
        var result = new byte[4 + nonce.Length + tag.Length + ciphertext.Length];
        BitConverter.GetBytes(result.Length).CopyTo(result, 0);
        nonce.CopyTo(result, 4);
        tag.CopyTo(result, 4 + nonce.Length);
        ciphertext.CopyTo(result, 4 + nonce.Length + tag.Length);
        return result;
    }

    // Decrypt a MAVLink packet
    public byte[]? Decrypt(byte[] encrypted)
    {
        if (_key == null || !IsEncryptionEnabled) return encrypted;
        try
        {
            using var aes = new AesGcm(_key, AesGcm.TagByteSizes.MaxSize);
            var nonceLen = AesGcm.NonceByteSizes.MaxSize;
            var tagLen = AesGcm.TagByteSizes.MaxSize;
            var nonce = encrypted[4..(4 + nonceLen)];
            var tag = encrypted[(4 + nonceLen)..(4 + nonceLen + tagLen)];
            var ciphertext = encrypted[(4 + nonceLen + tagLen)..];
            var plaintext = new byte[ciphertext.Length];
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        catch { return null; }
    }

    // Verify license signature (simplified — in production use RSA signing)
    public bool VerifyLicense(DeviceLicense license)
    {
        var expectedDeviceId = GetDeviceId();
        return license.DeviceId == expectedDeviceId && license.IsValid;
    }
}
