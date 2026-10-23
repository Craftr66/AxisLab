export interface Pose { x: number; y: number; z: number; rx: number; ry: number; rz: number }
export interface Parameters {
  width: number; height: number; fx: number; fy: number; cx: number; cy: number;
  sizeX: number; sizeY: number; sizeZ: number;
}
export interface Device {
  id: string; typeId: string; name: string; deviceNumber: number; parentId: string | null;
  transform: Pose; parameters: Parameters; configurationRevision: number; visible: boolean;
  attachmentFrameId: 'base' | 'flange' | 'tool';
  robot: { modelId: string; jointPositions: number[]; toolTransform: Pose; workTransform: Pose; geometry: RobotGeometry } | null;
  conveyor?: {speed:number}|null; gripper?: {graspRange:number}|null;
  photoelectric?: {cameraId:string|null;stopConveyorId:string|null;enabled:boolean}|null;
}
export interface RobotGeometry {
  baseHeight: number; shoulderHeight: number; upperArm: number; forearm: number; wrist1: number; wrist2: number; flangeLength: number;
  joint1Diameter: number; joint2Diameter: number; joint3Diameter: number; joint4Diameter: number; joint5Diameter: number; joint6Diameter: number;
}
export interface TeachPoint { id: string; number: number; robotId: string; name: string; joints: number[]; frame: string; target: Pose; dependencySignature: string }
export interface TeachPointView { point: TeachPoint; stale: boolean; reason: string | null }
export interface CellStep {name:string;action:string;deviceId:string|null;pointId:string|null;value:number;timeout:number}
export interface CellSequence {id:string;name:string;robotId:string;conveyorId:string;sensorId:string;gripperId:string;cameraId:string;steps:CellStep[];speedPercent:number}
export interface Project { schemaVersion: number; id: string; name: string; nextDeviceNumber: number; devices: Device[]; teachPoints: TeachPoint[]; nextTeachPointNumber: number;cellSequence?:CellSequence|null }
export interface Frame { pose: Pose; matrix: number[] }
export interface RobotState {
  deviceId: string; poseId: number; joints: number[]; status: 'Idle' | 'Running' | 'Completed' | 'Cancelled' | 'Failed';
  commandId: string | null; progress: number; jointMatrices: number[][];
  flangeBase: Frame; tcpBase: Frame; flangeWorld: Frame; tcpWorld: Frame;
  controlOwnerId: string | null; controlRemainingMilliseconds: number;
  workWorld: Frame; motionKind: string; stepIndex: number; stepCount: number; motionMessage: string | null; motionErrorCode: string | null; activeTeachPointId: string | null;
}
export interface RuntimeSnapshot {
  sessionId: string; projectRevision: number; robots: RobotState[]; devices: { deviceId: string; world: Frame }[];
  instruments?:{deviceId:string;running:boolean;input:boolean;closed:boolean;partId:string|null;risingEdges:number;speed:number}[];
  cell?:{status:string;stepIndex:number;stepCount:number;message:string;errorCode:string|null;elapsed:number;heldPartId:string|null};
}
export interface JointDefinition { name: string; axis: string; minimum: number; maximum: number; maxVelocity: number }
export interface Snapshot { sessionId: string; revision: number; dirty: boolean; project: Project; hasSavedFile: boolean; runtime: RuntimeSnapshot; teaching: TeachPointView[] }
export interface RendererLease { rendererId: string; token: string; leaseSeconds: number }
export interface CaptureState { captureId: string; cameraId: string; status: 'Queued' | 'Rendering' | 'Completed' | 'Failed'; frameId: string | null; errorCode: string | null; message: string | null }
export interface CaptureWork { captureId: string; exposureAt: string; snapshot: { sessionId: string; sceneRevision: number; camera: Device; project: Project; runtime: RuntimeSnapshot } }
export interface FrameMetadata {
  frameId: string; captureId: string; cameraId: string; cameraName: string; sessionId: string; sceneRevision: number;
  cameraConfigurationRevision: number; exposureAt: string; completedAt: string; width: number; height: number;
  encoding: string; sha256: string; cameraWorld: Frame; intrinsics: Parameters; robots: RobotState[]; imageUrl: string;
}
export const typeNames: Record<string, string> = {
  'robot.generic6r': '六轴机械臂', 'camera.pinhole': '针孔相机', 'geometry.box': '长方体',
  'robot.generic3r': '三轴机械臂',
  'vision.dotTarget': '圆点标定靶', 'vision.dotGrid': '九点标定板', 'vision.orientedPart': '方向工件',
  'mechanism.conveyor':'传送带','tool.gripper':'平行夹爪','sensor.photoelectric':'光电传感器','material.workpiece':'可抓取工件',
};
export const kind = (typeId: string) => typeId.startsWith('robot') ? 'robot' : typeId.startsWith('camera') ? 'camera' : 'box';
