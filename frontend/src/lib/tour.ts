/**
 * FactoryBrain homepage tour: copy, detections, floor status and camera
 * keyframes in one place. Edit words and camera positions here; the 3D
 * scene, the HTML cards and the floor rail all read from this file.
 *
 * Every figure on the page (readings, detection tags, the status list) is
 * illustrative. Anything carrying a number is flagged `example: true` and
 * rendered with `data-example="true"` so real figures can replace it later.
 */

export type Vec3 = [number, number, number];

/** Flag for the whole page: while true, a footnote says the figures are examples. */
export const EXAMPLE_CONTENT = true;

/* ------------------------------------------------------------------ */
/* Building dimensions (shared by the scene and the camera maths)      */
/* ------------------------------------------------------------------ */

export const BUILDING = {
  floorHeight: 4.2,
  floors: 5,
  /** Half extents of the floor plate. Front glass faces +z. */
  halfWidth: 18,
  halfDepth: 11,
  /** Column grid on the front and back facades. */
  columnXs: [-18, -9, 0, 9, 18],
  /** Interior column row. */
  interiorColumnZ: -5.5,
  interiorColumnXs: [-9, 0, 9],
} as const;

export const floorBase = (floorIndex: number) => floorIndex * BUILDING.floorHeight;

/* ------------------------------------------------------------------ */
/* Content                                                             */
/* ------------------------------------------------------------------ */

export type Pill = "OK" | "Watch" | "Act";

export interface Detection {
  /** World-space centre of the outlined box. */
  center: Vec3;
  /** Box size in metres. */
  size: Vec3;
  /** Mono label shown in the HTML tag. */
  label: string;
  example: true;
}

export interface FloorStatus {
  floor: string;
  /** Short name used on chips ("Sewing"). */
  short: string;
  name: string;
  issue: string;
  pill: Pill;
  example: true;
}

export interface FloorSection {
  kind: "floor";
  /** 1..5, also the camera keyframe index. */
  step: number;
  /** Building floor index 0..4 (0 = ground). */
  floorIndex: number;
  floor: string;
  label: string;
  heading: string;
  body: string;
  reading: { text: string; example: true };
  detection: Detection;
}

export interface OverviewSection {
  kind: "overview";
  step: number;
  floor: string;
  label: string;
  heading: string;
  body: string;
}

export type TourSection = FloorSection | OverviewSection;

export const HERO = {
  headlineLead: "Your whole factory.",
  headlineAccent: "Floor by floor.",
  headlineTail: "In one view.",
  body:
    "FactoryBrain watches fabric, machines and work in progress on every floor of a garment factory, and tells your team where to look before a small problem becomes a missed shipment.",
  primaryCta: "Book a demo",
  secondaryCta: "Take the tour",
  skip: "Skip the tour",
  scrollCue: "Scroll to fly in",
};

const H = BUILDING.floorHeight;

export const FLOOR_SECTIONS: FloorSection[] = [
  {
    kind: "floor",
    step: 1,
    floorIndex: 0,
    floor: "G",
    label: "Floor G · Fabric store & inspection",
    heading: "Fabric inspection without the guesswork",
    body:
      "Cameras over each inspection table log every fault against the roll and the metre where it appeared. Points are scored the same way on every shift, so a roll is passed or held on evidence, not on who was at the table.",
    reading: {
      text: "Roll 118: 3 faults in 62 m, 19 points per 100 sq yd. Passed for cutting.",
      example: true,
    },
    detection: {
      center: [4.5, 1.12, 1.4],
      size: [0.7, 0.18, 0.5],
      label: "Slub · 2 pts · roll 118 @ 34.6 m",
      example: true,
    },
  },
  {
    kind: "floor",
    step: 2,
    floorIndex: 1,
    floor: "1",
    label: "Floor 1 · Cutting",
    heading: "Less fabric on the cutting-room floor",
    body:
      "Every lay is checked before the knife goes in: ply count, ply alignment and marker efficiency. A shifted ply is caught on the table, not in a bundle of mismatched panels three floors up.",
    reading: {
      text: "Table 3: ply shift of 6 mm caught before cutting. Marker efficiency 84.2%.",
      example: true,
    },
    detection: {
      center: [-4.2, H + 1.02, 5.5],
      size: [2.2, 0.22, 1.7],
      label: "Ply shift 6 mm · table 3",
      example: true,
    },
  },
  {
    kind: "floor",
    step: 3,
    floorIndex: 2,
    floor: "2",
    label: "Floor 2 · Sewing lines",
    heading: "Find the bottleneck while the line is still running",
    body:
      "Machine data and overhead cameras follow work down each line. When bundles start to pile up at one operation, the supervisor sees it within minutes, while there is still time to rebalance.",
    reading: {
      text: "Line 7, op 14: 46 bundles waiting, about 38 minutes of work. Suggested: move one operator from op 11.",
      example: true,
    },
    detection: {
      center: [4.6, 2 * H + 0.45, 3.35],
      size: [1.1, 0.9, 0.8],
      label: "WIP pile-up · op 14 · line 7",
      example: true,
    },
  },
  {
    kind: "floor",
    step: 4,
    floorIndex: 3,
    floor: "3",
    label: "Floor 3 · Finishing & QC",
    heading: "Quality problems traced back to where they started",
    body:
      "Every defect found at the QC table is linked to the line, the operation and the hour it was sewn. Instead of a weekly defect report, supervisors get a short list of where problems are starting today.",
    reading: {
      text: "Open seams on line 4 rose from 0.8% to 3.1% in hour 3. Traced to one machine due a needle change.",
      example: true,
    },
    detection: {
      center: [-4.5, 3 * H + 1.02, 5.2],
      size: [0.8, 0.14, 0.7],
      label: "Open seam · line 4 · hour 3",
      example: true,
    },
  },
  {
    kind: "floor",
    step: 5,
    floorIndex: 4,
    floor: "4",
    label: "Floor 4 · Packing & dispatch",
    heading: "Know early if the shipment will make it",
    body:
      "Cartons are counted as they leave the conveyor and matched to each purchase order. If packing falls behind the ex-factory date, merchandising hears about it days before the truck is booked.",
    reading: {
      text: "PO 4471: 341 of 400 cartons packed. At the current rate, ready 6 hours before the truck.",
      example: true,
    },
    detection: {
      center: [4.6, 4 * H + 0.85, 1.2],
      size: [2.6, 1.7, 1.4],
      label: "PO 4471 · 341 / 400 cartons",
      example: true,
    },
  },
];

export const OVERVIEW_SECTION: OverviewSection = {
  kind: "overview",
  step: 6,
  floor: "ALL",
  label: "All floors",
  heading: "One brain for the whole building",
  body:
    "Each floor reports into one view. The day starts with a short list of what needs attention, ranked by what it puts at risk.",
};

export const TOUR_SECTIONS: TourSection[] = [...FLOOR_SECTIONS, OVERVIEW_SECTION];

/** Top-down, as the building reads. */
export const FLOOR_STATUS: FloorStatus[] = [
  { floor: "4", short: "Packing", name: "Packing & dispatch", issue: "PO 4471 at 341 of 400 cartons", pill: "Watch", example: true },
  { floor: "3", short: "QC", name: "Finishing & QC", issue: "Open seams up on line 4", pill: "Watch", example: true },
  { floor: "2", short: "Sewing", name: "Sewing lines", issue: "WIP pile-up at op 14, line 7", pill: "Act", example: true },
  { floor: "1", short: "Cutting", name: "Cutting", issue: "Ply shift on table 3, corrected", pill: "OK", example: true },
  { floor: "G", short: "Fabric", name: "Fabric store & inspection", issue: "Roll 118 graded and released", pill: "OK", example: true },
];

export const statusForFloorIndex = (floorIndex: number) =>
  FLOOR_STATUS.find((s) => s.floor === (floorIndex === 0 ? "G" : String(floorIndex)))!;

export const HOW_IT_WORKS = {
  heading: "How it works",
  steps: [
    {
      title: "Connect",
      body:
        "We fit cameras over inspection tables, cutting tables, line ends and the packing conveyor, and read the machine data you already collect. Nobody on the floor changes how they work.",
    },
    {
      title: "Learn",
      body:
        "For the first weeks FactoryBrain learns what normal looks like for your styles, lines and shifts, so it flags real problems instead of noise.",
    },
    {
      title: "Act",
      body:
        "Supervisors get short alerts with the evidence attached: the photo, the line, the hour. Managers get one daily view of the whole building.",
    },
  ],
  watches: "fabric, machines, WIP, cartons",
  doesNotWatch: "individual workers",
};

/** Floor rail, top to bottom. `step` is the camera keyframe it flies to. */
export const RAIL_STOPS = [
  { key: "ALL", step: 6, name: "All floors" },
  { key: "4", step: 5, name: "Packing & dispatch" },
  { key: "3", step: 4, name: "Finishing & QC" },
  { key: "2", step: 3, name: "Sewing lines" },
  { key: "1", step: 2, name: "Cutting" },
  { key: "G", step: 1, name: "Fabric store & inspection" },
  { key: "OUT", step: 0, name: "Outside" },
] as const;

export const LAST_STEP = 6;

/* ------------------------------------------------------------------ */
/* Camera keyframes                                                    */
/* ------------------------------------------------------------------ */

export interface CameraPose {
  pos: Vec3;
  target: Vec3;
}

export interface CameraKey {
  desktop: CameraPose;
  portrait: CameraPose;
}

export const FOV = { desktop: 42, portrait: 64 };

/** Width / height below which the portrait framing is used. */
export const PORTRAIT_ASPECT = 0.9;

/** Distance of the drone in front of the glass (glass sits at z = halfDepth). */
const DRONE_Z = { desktop: 16.4, portrait: 19.5 };

/**
 * Nudges a camera x so the sightline to `focus` doesn't cross the front
 * facade right behind a column. Columns are 0.6 m wide; keep 1.1 m clear.
 */
export function avoidColumns(camX: number, camZ: number, focus: Vec3): number {
  const glassZ = BUILDING.halfDepth;
  const clear = 1.1;
  let x = camX;
  for (let i = 0; i < 4; i++) {
    const t = (camZ - glassZ) / (camZ - focus[2]);
    const crossX = x + (focus[0] - x) * t;
    const hit = BUILDING.columnXs.find((c) => Math.abs(crossX - c) < clear);
    if (hit === undefined) break;
    // Push the crossing to the nearer side of the column.
    const dir = crossX >= hit ? 1 : -1;
    const needed = hit + dir * clear - crossX;
    x += needed / (1 - t);
  }
  return x;
}

function floorKey(section: FloorSection): CameraKey {
  const [fx, , fz] = section.detection.center;
  const y0 = floorBase(section.floorIndex);
  const dx = fx > 0 ? -2.6 : -2.2;
  const deskX = avoidColumns(fx + dx, DRONE_Z.desktop, section.detection.center);
  const portX = avoidColumns(fx, DRONE_Z.portrait, section.detection.center);
  return {
    desktop: {
      pos: [deskX, y0 + 2.75, DRONE_Z.desktop],
      target: [fx - 2.1, y0 + 1.0, fz - 1],
    },
    portrait: {
      pos: [portX, y0 + 3.0, DRONE_Z.portrait],
      target: [fx, y0 - 1.5, fz - 1],
    },
  };
}

export const CAMERA_KEYS: CameraKey[] = [
  // 0: wide hero view from the front-left; building on the right half.
  {
    desktop: { pos: [-60, 8, 66], target: [-19, 10.5, 2] },
    // Down the road, looking up, so the building sits low under the hero
    // text. Stays in front of the neighbour blocks across the road (z > 72).
    portrait: { pos: [-80, 3, 70], target: [-1.2, 56, -2.4] },
  },
  // 1..5: just outside each floor's glass.
  ...FLOOR_SECTIONS.map(floorKey),
  // 6: high pull-back from the front-right showing the whole building.
  {
    desktop: { pos: [50, 40, 56], target: [-7, 8.5, 0] },
    portrait: { pos: [38, 50, 84], target: [1, 1, 0] },
  },
];

/** Fraction of each segment where the camera holds still at either end. */
export const PLATEAU = 0.18;

const clamp01 = (v: number) => (v < 0 ? 0 : v > 1 ? 1 : v);

/** Ease with a rest at both ends of the segment. */
export function plateauEase(t: number): number {
  const u = clamp01((t - PLATEAU) / (1 - 2 * PLATEAU));
  return u < 0.5 ? 4 * u * u * u : 1 - Math.pow(-2 * u + 2, 3) / 2;
}

/** Sideways (away from the glass) bulge when climbing between floors. */
const CLIMB_ARC = 3.2;

/**
 * Camera pose for a continuous progress value p (0..LAST_STEP). Pure maths,
 * so the floor rail can derive altitude without loading three.js.
 */
export function cameraAt(p: number, portrait: boolean, out?: CameraPose): CameraPose {
  const res: CameraPose = out ?? { pos: [0, 0, 0], target: [0, 0, 0] };
  const clamped = Math.max(0, Math.min(LAST_STEP, p));
  const i = Math.min(LAST_STEP - 1, Math.floor(clamped));
  const t = clamped - i;
  const e = plateauEase(t);
  const a = portrait ? CAMERA_KEYS[i].portrait : CAMERA_KEYS[i].desktop;
  const b = portrait ? CAMERA_KEYS[i + 1].portrait : CAMERA_KEYS[i + 1].desktop;
  for (let k = 0; k < 3; k++) {
    res.pos[k] = a.pos[k] + (b.pos[k] - a.pos[k]) * e;
    res.target[k] = a.target[k] + (b.target[k] - a.target[k]) * e;
  }
  // Floor-to-floor climbs (segments 1→2 … 4→5) arc out from the glass.
  if (i >= 1 && i <= 4) {
    const arc = Math.sin(Math.PI * e) * CLIMB_ARC;
    res.pos[2] += arc;
    res.target[2] += arc * 0.35;
  }
  return res;
}
