namespace HobbywingCan.Core.DroneCan;

/// <summary>A CAN frame with a 29-bit (extended) or 11-bit identifier, without flag bits.</summary>
public sealed record CanFrame(uint Id, byte[] Data, bool Extended = true)
{
    public override string ToString() => $"{Id:X8} [{Data.Length}] {Convert.ToHexString(Data)}";
}

/// <summary>Transport that can exchange raw CAN frames with the bus.</summary>
public interface ICanBus
{
    event Action<CanFrame>? FrameReceived;

    void Send(CanFrame frame);
}
