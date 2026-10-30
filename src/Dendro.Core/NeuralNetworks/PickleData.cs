using System.Buffers.Binary;
using System.Text;

namespace Dendro.Core.NeuralNetworks;

/// <summary>Data-only protocol 4/5 reader. GLOBAL, REDUCE and BUILD stay inert; no Python runtime or callable is loaded.</summary>
internal sealed class PickleValue(int kind)
{
    public int Kind { get; } = kind;
    public double Number { get; set; }
    public byte[] Bytes { get; set; } = [];
    public string Text => Encoding.UTF8.GetString(Bytes);
    public List<PickleValue> Items { get; } = [];
    public PickleValue? Call { get; set; }
    public PickleValue? State { get; set; }

    public PickleValue? At(int index) =>
        Kind == 4 && index >= 0 && index < Items.Count ? Items[index] : null;

    public PickleValue? Get(string key)
    {
        var v = Kind == 7 ? State : this;
        if (v?.Kind != 5)
            return null;
        for (int i = 0; i + 1 < v.Items.Count; i += 2)
            if (v.Items[i].Kind == 2 && v.Items[i].Text == key)
                return v.Items[i + 1];
        return null;
    }

    public string ClassName => Call?.Items.Count == 2 ? Call.Items[1].Text : "";
    public PickleValue? ArrayData => Kind == 7 ? State?.At(4) : null;

    public string[] Strings() =>
        Items
            .Select(v =>
                v.Kind == 2
                    ? v.Text
                    : throw new InvalidDataException("Lista pickle de textos inválida.")
            )
            .ToArray();

    public double[] Doubles(int count)
    {
        var dtype = State?.At(2);
        var data = ArrayData;
        if (
            dtype?.Items.FirstOrDefault()?.At(0)?.Text != "f8"
            || dtype.State?.At(1)?.Text != "<"
            || data?.Kind != 3
            || data.Bytes.Length != count * 8
        )
            throw new InvalidDataException("StandardScaler incompatível com as dimensões da rede.");
        var result = new double[count];
        for (int i = 0; i < count; i++)
            result[i] = BinaryPrimitives.ReadDoubleLittleEndian(data.Bytes.AsSpan(i * 8, 8));
        if (result.Any(v => !double.IsFinite(v)))
            throw new InvalidDataException("Normalização não finita.");
        return result;
    }
}

internal static class PickleData
{
    public static PickleValue Read(byte[] bytes)
    {
        if (bytes.Length is < 2 or > 16777216)
            throw new InvalidDataException("Pré-processamento vazio ou acima do limite.");
        using var reader = new BinaryReader(new MemoryStream(bytes));
        var stack = new List<PickleValue>();
        var memo = new PickleValue?[8192];
        int nextMemo = 0,
            objects = 0;
        PickleValue Make(int kind)
        {
            if (++objects > 100000)
                throw new InvalidDataException("Limite de objetos pickle excedido.");
            return new(kind);
        }
        PickleValue Pop()
        {
            if (stack.Count == 0)
                throw new InvalidDataException("Pilha pickle inválida.");
            var v = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            return v;
        }
        void Push(PickleValue v)
        {
            if (stack.Count >= 8192)
                throw new InvalidDataException("Limite de pilha pickle excedido.");
            stack.Add(v);
        }
        int Mark()
        {
            int i = stack.FindLastIndex(v => v.Kind == 8);
            return i >= 0 ? i : throw new InvalidDataException("Marca pickle ausente.");
        }
        int Length(int n) =>
            n switch
            {
                1 => reader.ReadByte(),
                4 => checked((int)reader.ReadUInt32()),
                8 => checked((int)reader.ReadUInt64()),
                _ => throw new InvalidDataException(),
            };
        while (reader.BaseStream.Position < bytes.Length)
        {
            byte op = reader.ReadByte();
            PickleValue a,
                b,
                v;
            int n,
                index;
            switch (op)
            {
                case 0x80:
                    if (reader.ReadByte() is < 4 or > 5)
                        throw new InvalidDataException("Protocolo pickle incompatível.");
                    break;
                case 0x95:
                    if (reader.ReadUInt64() > (ulong)(bytes.Length - reader.BaseStream.Position))
                        throw new InvalidDataException("Quadro pickle truncado.");
                    break;
                case (byte)'.':
                    v = Pop();
                    if (stack.Count != 0)
                        throw new InvalidDataException("Pilha pickle não vazia.");
                    return v;
                case (byte)'N':
                    Push(Make(0));
                    break;
                case 0x88:
                case 0x89:
                case (byte)'K':
                case (byte)'M':
                case (byte)'J':
                    v = Make(1);
                    v.Number = op switch
                    {
                        0x88 => 1,
                        0x89 => 0,
                        (byte)'K' => reader.ReadByte(),
                        (byte)'M' => reader.ReadUInt16(),
                        _ => reader.ReadInt32(),
                    };
                    Push(v);
                    break;
                case (byte)'G':
                    v = Make(1);
                    v.Number = BinaryPrimitives.ReadDoubleBigEndian(reader.ReadBytes(8));
                    Push(v);
                    break;
                case 0x8c:
                case (byte)'X':
                case 0x8d:
                case (byte)'C':
                case (byte)'B':
                case 0x8e:
                case 0x96:
                    n = Length(
                        op is 0x8c or (byte)'C' ? 1
                        : op is (byte)'X' or (byte)'B' ? 4
                        : 8
                    );
                    if (n < 0 || n > bytes.Length - reader.BaseStream.Position)
                        throw new InvalidDataException("Texto pickle truncado.");
                    v = Make(op is 0x8c or (byte)'X' or 0x8d ? 2 : 3);
                    v.Bytes = reader.ReadBytes(n);
                    Push(v);
                    break;
                case (byte)']':
                case (byte)')':
                    Push(Make(4));
                    break;
                case (byte)'}':
                    Push(Make(5));
                    break;
                case (byte)'(':
                    Push(Make(8));
                    break;
                case 0x94:
                    if (nextMemo >= memo.Length)
                        throw new InvalidDataException("Memória pickle excedida.");
                    memo[nextMemo++] = stack[^1];
                    break;
                case (byte)'q':
                case (byte)'r':
                    index = Length(op == (byte)'q' ? 1 : 4);
                    if (index >= memo.Length)
                        throw new InvalidDataException("Índice pickle inválido.");
                    memo[index] = stack[^1];
                    nextMemo = Math.Max(nextMemo, index + 1);
                    break;
                case (byte)'h':
                case (byte)'j':
                    index = Length(op == (byte)'h' ? 1 : 4);
                    Push(
                        index < memo.Length
                            ? memo[index]
                                ?? throw new InvalidDataException("Referência pickle ausente.")
                            : throw new InvalidDataException("Índice pickle inválido.")
                    );
                    break;
                case 0x93:
                    b = Pop();
                    a = Pop();
                    if (a.Kind != 2 || b.Kind != 2)
                        throw new InvalidDataException("GLOBAL pickle inválido.");
                    v = Make(6);
                    v.Items.AddRange([a, b]);
                    Push(v);
                    break;
                case (byte)'R':
                case 0x81:
                    b = Pop();
                    a = Pop();
                    v = Make(7);
                    v.Call = a;
                    v.Items.Add(b);
                    Push(v);
                    break;
                case (byte)'b':
                    b = Pop();
                    stack[^1].State = b;
                    break;
                case 0x85:
                case 0x86:
                case 0x87:
                    n = op - 0x84;
                    v = Make(4);
                    index = stack.Count - n;
                    v.Items.AddRange(stack.GetRange(index, n));
                    stack.RemoveRange(index, n);
                    Push(v);
                    break;
                case (byte)'t':
                case (byte)'l':
                case (byte)'d':
                    index = Mark();
                    v = Make(op == (byte)'d' ? 5 : 4);
                    v.Items.AddRange(stack.Skip(index + 1));
                    stack.RemoveRange(index, stack.Count - index);
                    Push(v);
                    break;
                case (byte)'e':
                case (byte)'u':
                    index = Mark();
                    if (index == 0)
                        throw new InvalidDataException("Container pickle ausente.");
                    stack[index - 1].Items.AddRange(stack.Skip(index + 1).ToArray());
                    stack.RemoveRange(index, stack.Count - index);
                    break;
                case (byte)'a':
                    b = Pop();
                    stack[^1].Items.Add(b);
                    break;
                case (byte)'s':
                    b = Pop();
                    a = Pop();
                    stack[^1].Items.AddRange([a, b]);
                    break;
                default:
                    throw new InvalidDataException(
                        $"Dados pickle incompatíveis (opcode 0x{op:x2}, posição {reader.BaseStream.Position})."
                    );
            }
        }
        throw new InvalidDataException("Dados pickle sem terminador.");
    }
}
