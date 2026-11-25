using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Dendro.Core.Tabular;

/// <summary>
/// Holds a random per-file data key. Password-derived key material is used only
/// to wrap this key, so changing the project password does not require rewriting
/// every table block.
/// </summary>
public sealed class DlbEncryptionKey : IDisposable
{
    internal const int SaltBytes = 16;
    internal const int NonceBytes = 12;
    internal const int TagBytes = 16;
    internal const int KeyBytes = 32;
    internal const int DefaultIterations = 600_000;
    internal const int HeaderPayloadBytes = SaltBytes + 4 + NonceBytes + TagBytes + KeyBytes;
    internal const int FrameBytes = 1024 * 1024;
    private static readonly byte[] WrapAad = "DENDRO-DLB-DATA-KEY-V5"u8.ToArray();
    private readonly byte[] key;
    private bool disposed;

    private DlbEncryptionKey(byte[] key, byte[] salt, int iterations, byte[] wrapNonce, byte[] wrappedKey, byte[] wrapTag)
    {
        this.key = key;
        Salt = salt;
        Iterations = iterations;
        WrapNonce = wrapNonce;
        WrappedKey = wrappedKey;
        WrapTag = wrapTag;
    }

    internal byte[] Salt { get; }
    internal int Iterations { get; }
    internal byte[] WrapNonce { get; }
    internal byte[] WrappedKey { get; }
    internal byte[] WrapTag { get; }
    internal ReadOnlySpan<byte> DataKey => disposed ? throw new ObjectDisposedException(nameof(DlbEncryptionKey)) : key;

    public static DlbEncryptionKey Create(ReadOnlySpan<char> password)
    {
        ValidatePassword(password);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] dataKey = RandomNumberGenerator.GetBytes(KeyBytes);
        byte[] kek = Rfc2898DeriveBytes.Pbkdf2(password, salt, DefaultIterations, HashAlgorithmName.SHA256, KeyBytes);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        byte[] wrapped = new byte[KeyBytes];
        byte[] tag = new byte[TagBytes];
        try
        {
            using var aes = new AesGcm(kek, TagBytes);
            aes.Encrypt(nonce, dataKey, wrapped, tag, WrapAad);
            return new(dataKey, salt, DefaultIterations, nonce, wrapped, tag);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(dataKey);
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    internal static DlbEncryptionKey Unlock(ReadOnlySpan<char> password, ReadOnlySpan<byte> header)
    {
        ValidatePassword(password);
        if (header.Length != HeaderPayloadBytes) throw new InvalidDataException("Cabeçalho criptográfico DLB incompleto.");
        int offset = 0;
        byte[] salt = header.Slice(offset, SaltBytes).ToArray(); offset += SaltBytes;
        int iterations = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(offset, 4)); offset += 4;
        if (iterations is < 100_000 or > 2_000_000) throw new InvalidDataException("Parâmetros de derivação da chave DLB inválidos.");
        byte[] nonce = header.Slice(offset, NonceBytes).ToArray(); offset += NonceBytes;
        byte[] tag = header.Slice(offset, TagBytes).ToArray(); offset += TagBytes;
        byte[] wrapped = header.Slice(offset, KeyBytes).ToArray();
        byte[] kek = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
        byte[] dataKey = new byte[KeyBytes];
        try
        {
            using var aes = new AesGcm(kek, TagBytes);
            aes.Decrypt(nonce, wrapped, tag, dataKey, WrapAad);
            return new(dataKey, salt, iterations, nonce, wrapped, tag);
        }
        catch (CryptographicException ex)
        {
            CryptographicOperations.ZeroMemory(dataKey);
            throw new CryptographicException("Senha incorreta ou cabeçalho DLB adulterado.", ex);
        }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    public static bool IsEncryptedFile(string path)
    {
        if (!File.Exists(path)) return false;
        Span<byte> magic = stackalloc byte[8];
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Read(magic) != magic.Length) return false;
        return magic.SequenceEqual(CompactBankFormat.EncryptedMagic);
    }

    public static DlbEncryptionKey UnlockFile(string path, ReadOnlySpan<char> password)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> magic = stackalloc byte[8];
        input.ReadExactly(magic);
        if (!magic.SequenceEqual(CompactBankFormat.EncryptedMagic))
            throw new InvalidDataException("O arquivo não é um DLB criptografado compatível.");
        byte[] header = new byte[HeaderPayloadBytes];
        input.ReadExactly(header);
        return Unlock(password, header);
    }

    internal byte[] HeaderPayload()
    {
        var result = new byte[HeaderPayloadBytes];
        int offset = 0;
        Salt.CopyTo(result, offset); offset += SaltBytes;
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset, 4), Iterations); offset += 4;
        WrapNonce.CopyTo(result, offset); offset += NonceBytes;
        WrapTag.CopyTo(result, offset); offset += TagBytes;
        WrappedKey.CopyTo(result, offset);
        return result;
    }

    internal byte[] EncryptCatalog(ReadOnlySpan<byte> plaintext, long generation)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        byte[] tag = new byte[TagBytes];
        byte[] cipher = new byte[plaintext.Length];
        using var aes = new AesGcm(DataKey, TagBytes);
        aes.Encrypt(nonce, plaintext, cipher, tag, CatalogAad(generation));
        var result = new byte[NonceBytes + TagBytes + cipher.Length];
        nonce.CopyTo(result, 0); tag.CopyTo(result, NonceBytes); cipher.CopyTo(result, NonceBytes + TagBytes);
        CryptographicOperations.ZeroMemory(cipher);
        return result;
    }

    internal byte[] DecryptCatalog(ReadOnlySpan<byte> envelope, long generation)
    {
        if (envelope.Length <= NonceBytes + TagBytes) throw new InvalidDataException("Catálogo DLB criptografado inválido.");
        byte[] plaintext = new byte[envelope.Length - NonceBytes - TagBytes];
        using var aes = new AesGcm(DataKey, TagBytes);
        try { aes.Decrypt(envelope[..NonceBytes], envelope[(NonceBytes + TagBytes)..], envelope.Slice(NonceBytes, TagBytes), plaintext, CatalogAad(generation)); }
        catch (CryptographicException ex) { CryptographicOperations.ZeroMemory(plaintext); throw new InvalidDataException("Catálogo DLB não autenticado.", ex); }
        return plaintext;
    }

    internal void EncryptFrames(Stream destination, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        using var aes = new AesGcm(DataKey, TagBytes);
        int frameIndex = 0;
        Span<byte> length = stackalloc byte[4];
        while (!plaintext.IsEmpty)
        {
            int count = Math.Min(FrameBytes, plaintext.Length);
            ReadOnlySpan<byte> frame = plaintext[..count];
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            byte[] cipher = new byte[count];
            byte[] tag = new byte[TagBytes];
            aes.Encrypt(nonce, frame, cipher, tag, FrameAad(associatedData, frameIndex, count));
            BinaryPrimitives.WriteInt32LittleEndian(length, count);
            destination.Write(length); destination.Write(nonce); destination.Write(tag); destination.Write(cipher);
            CryptographicOperations.ZeroMemory(cipher);
            plaintext = plaintext[count..];
            frameIndex = checked(frameIndex + 1);
        }
    }

    internal Stream DecryptFrames(Stream ciphertext, ReadOnlySpan<byte> associatedData) =>
        new AuthenticatedFrameStream(ciphertext, key, associatedData.ToArray());

    internal static byte[] SegmentAad(long firstRow, int segmentIndex)
    {
        byte[] result = new byte[28];
        "DENDRO-DLB-SEG-V"u8.CopyTo(result);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(16, 8), firstRow);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(24, 4), segmentIndex);
        return result;
    }

    private static byte[] CatalogAad(long generation)
    {
        byte[] aad = new byte[24];
        "DENDRO-DLB-CAT-V"u8.CopyTo(aad);
        BinaryPrimitives.WriteInt64LittleEndian(aad.AsSpan(16), generation);
        return aad;
    }

    private static byte[] FrameAad(ReadOnlySpan<byte> baseAad, int frameIndex, int frameLength)
    {
        byte[] aad = new byte[baseAad.Length + 8];
        baseAad.CopyTo(aad);
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(baseAad.Length), frameIndex);
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(baseAad.Length + 4), frameLength);
        return aad;
    }

    private static void ValidatePassword(ReadOnlySpan<char> password)
    {
        if (password.Length < 12) throw new ArgumentException("Use uma senha com pelo menos 12 caracteres.", nameof(password));
    }

    public void Dispose()
    {
        if (disposed) return;
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(Salt);
        CryptographicOperations.ZeroMemory(WrapNonce);
        CryptographicOperations.ZeroMemory(WrappedKey);
        CryptographicOperations.ZeroMemory(WrapTag);
        disposed = true;
    }

    private sealed class AuthenticatedFrameStream : Stream
    {
        private readonly Stream source;
        private readonly byte[] key;
        private readonly byte[] aad;
        private byte[] frame = [];
        private int position;
        private int frameIndex;
        private bool ended;

        internal AuthenticatedFrameStream(Stream source, byte[] key, byte[] aad)
        {
            this.source = source; this.key = key; this.aad = aad;
        }

        private bool LoadFrame()
        {
            if (ended) return false;
            Span<byte> lengthBytes = stackalloc byte[4];
            int first = source.Read(lengthBytes[..1]);
            if (first == 0) { ended = true; return false; }
            source.ReadExactly(lengthBytes[1..]);
            int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            if (length is < 1 or > FrameBytes) throw new InvalidDataException("Quadro criptográfico DLB inválido.");
            byte[] nonce = new byte[NonceBytes], tag = new byte[TagBytes], cipher = new byte[length];
            source.ReadExactly(nonce); source.ReadExactly(tag); source.ReadExactly(cipher);
            CryptographicOperations.ZeroMemory(frame);
            frame = new byte[length]; position = 0;
            try
            {
                using var aes = new AesGcm(key, TagBytes);
                aes.Decrypt(nonce, cipher, tag, frame, FrameAad(aad, frameIndex, length));
            }
            catch (CryptographicException ex)
            {
                CryptographicOperations.ZeroMemory(frame);
                throw new InvalidDataException("Bloco DLB não autenticado; a senha não confere ou os dados foram alterados.", ex);
            }
            finally { CryptographicOperations.ZeroMemory(cipher); }
            frameIndex = checked(frameIndex + 1);
            return true;
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            if (position == frame.Length && !LoadFrame()) return 0;
            int count = Math.Min(buffer.Length, frame.Length - position);
            frame.AsSpan(position, count).CopyTo(buffer); position += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        protected override void Dispose(bool disposing)
        {
            if (disposing) CryptographicOperations.ZeroMemory(frame);
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Key for the single active desktop project, shared with background callbacks.</summary>
public static class DlbEncryptionSession
{
    // The desktop application has one active project. Keep its key at session
    // scope so dispatcher timers and other callbacks can read encrypted DLBs
    // after the async open operation has completed.
    private static DlbEncryptionKey? key;
    public static DlbEncryptionKey? Current { get => key; set => key = value; }
    public static void Clear()
    {
        key?.Dispose();
        key = null;
    }
}
