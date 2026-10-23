namespace AxisLab.Core;

public record ConveyorSettings(double Speed = 80);
public record GripperSettings(double GraspRange = 15);
public record PhotoelectricSettings(Guid? CameraId = null, Guid? StopConveyorId = null, bool Enabled = true);
public record CellStep(string Name, string Action, Guid? DeviceId = null, Guid? PointId = null,
    double Value = 0, double Timeout = 30);
public record CellSequence(Guid Id, string Name, Guid RobotId, Guid ConveyorId, Guid SensorId,
    Guid GripperId, Guid CameraId, CellStep[] Steps, double SpeedPercent = 35);
public record CellRunState(string Status, int StepIndex, int StepCount, string Message, string? ErrorCode,
    double Elapsed, Guid? HeldPartId);
public record IndustrialState(Guid DeviceId, bool Running, bool Input, bool Closed, Guid? PartId,
    uint RisingEdges, double Speed);
public record CameraTriggerEvent(long Sequence, Guid SensorId, Guid CameraId, Guid SessionId,
    Guid? CaptureId = null, Guid? FrameId = null, string Status = "Pending", string? Error = null);
