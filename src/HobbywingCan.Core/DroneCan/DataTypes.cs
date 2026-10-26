namespace HobbywingCan.Core.DroneCan;

public sealed record DataTypeInfo(string Name, ushort Id, ulong Signature, bool IsService);

/// <summary>
/// DroneCAN data types used by the app. Signatures were taken from the official DSDL
/// (dronecan/DSDL, com.hobbywing.esc.* use OVERRIDE_SIGNATURE).
/// </summary>
public static class DataTypes
{
    public static readonly DataTypeInfo GetNodeInfo = new("uavcan.protocol.GetNodeInfo", 1, 0xEE468A8121C46A9E, true);
    public static readonly DataTypeInfo ParamGetSet = new("uavcan.protocol.param.GetSet", 11, 0xA7B622F939D1A4D5, true);
    public static readonly DataTypeInfo NodeStatus = new("uavcan.protocol.NodeStatus", 341, 0x0F0868D0C1A7C6F1, false);
    public static readonly DataTypeInfo EscStatus = new("uavcan.equipment.esc.Status", 1034, 0xA9AF28AEA2FBB254, false);
    public static readonly DataTypeInfo EscStatusExtended = new("uavcan.equipment.esc.StatusExtended", 1036, 0x02DC203C50960EDC, false);

    public static readonly DataTypeInfo HwSetId = new("com.hobbywing.esc.SetID", 210, 0xC323CB5E9EC2B6F7, true);
    public static readonly DataTypeInfo HwSetLed = new("com.hobbywing.esc.SetLED", 212, 0xB493BD48C0853EE5, true);
    public static readonly DataTypeInfo HwSetThrottleSource = new("com.hobbywing.esc.SetThrottleSource", 215, 0x0C248FAAEFE5E29A, true);
    public static readonly DataTypeInfo HwSetDirection = new("com.hobbywing.esc.SetDirection", 213, 0x9D793111D262BA68, true);
    public static readonly DataTypeInfo HwSetReportingFrequency = new("com.hobbywing.esc.SetReportingFrequency", 214, 0x1FD0404420983DEB, true);
    public static readonly DataTypeInfo HwGetMaintenanceInformation = new("com.hobbywing.esc.GetMaintenanceInformation", 241, 0xB81DBD4EC9A5977D, true);
    public static readonly DataTypeInfo HwGetMajorConfig = new("com.hobbywing.esc.GetMajorConfig", 242, 0x1506774DA3930BFD, true);
    public static readonly DataTypeInfo HwGetEscId = new("com.hobbywing.esc.GetEscID", 20013, 0x4E2D, false);
    public static readonly DataTypeInfo HwStatusMsg1 = new("com.hobbywing.esc.StatusMsg1", 20050, 0x0813B3E2C4AD670E, false);
    public static readonly DataTypeInfo HwStatusMsg2 = new("com.hobbywing.esc.StatusMsg2", 20051, 0x1675DA01C3B91297, false);
    public static readonly DataTypeInfo HwStatusMsg3 = new("com.hobbywing.esc.StatusMsg3", 20052, 0x24919CD1EB34ECE9, false);

    public static readonly IReadOnlyList<DataTypeInfo> All =
    [
        GetNodeInfo, ParamGetSet, NodeStatus, EscStatus, EscStatusExtended,
        HwSetId, HwSetLed, HwSetThrottleSource,
        HwSetDirection, HwSetReportingFrequency, HwGetMaintenanceInformation, HwGetMajorConfig,
        HwGetEscId, HwStatusMsg1, HwStatusMsg2, HwStatusMsg3,
    ];

    public static DataTypeInfo? Find(bool isService, ushort id) =>
        All.FirstOrDefault(t => t.IsService == isService && t.Id == id);
}
