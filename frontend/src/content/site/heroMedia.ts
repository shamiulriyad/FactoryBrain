/**
 * Homepage hero photographs: the single place that lists which images exist.
 *
 * To add a photo: put a 3:2 landscape JPEG (about 1800 x 1200, metadata
 * stripped) in /public/site/hero, add an entry below with an honest alt text
 * and its credit, and it becomes selectable in the app under
 * Administration → Website. Which photos are shown, and whether they rotate,
 * is stored in the API (GET /api/site-settings) and changed there, not here.
 *
 * Every photo below is a real photograph taken in Bangladesh, published on
 * Pexels under the Pexels License (free for commercial use). Keep the credits:
 * one photographer asks for a link back, and crediting is good practice.
 */

export interface HeroImage {
  id: string;
  src: string;
  alt: string;
  width: number;
  height: number;
  /** CSS object-position, to keep the subject in frame on narrow crops. */
  focus?: string;
  /** Short caption shown on the image. */
  caption: string;
  credit: { name: string; url: string; source: "Pexels"; sourceUrl: string };
}

export const HERO_IMAGES: HeroImage[] = [
  {
    id: "factory-floor-team",
    src: "/site/hero/factory-floor-team.jpg",
    alt: "Women in colourful headscarves sorting thread cones at a long table on a bright, modern factory floor in Bangladesh",
    width: 1800,
    height: 1200,
    focus: "55% 50%",
    caption: "Thread section, Bangladesh",
    credit: {
      name: "Galib Rahman Nadim",
      url: "https://www.pexels.com/@capturedbynadim/",
      source: "Pexels",
      sourceUrl: "https://www.pexels.com/photo/women-working-in-textile-factory-sorting-thread-spools-39351252/",
    },
  },
  {
    id: "sewing-workers",
    src: "/site/hero/sewing-workers.jpg",
    alt: "Two women working at sewing machines in a garment factory in Bangladesh, lit by daylight from the windows",
    width: 1800,
    height: 1200,
    focus: "40% 45%",
    caption: "Sewing, Bangladesh",
    credit: {
      name: "Ian Taylor",
      url: "https://iantaylor.ca/",
      source: "Pexels",
      sourceUrl: "https://www.pexels.com/photo/women-sewing-in-a-bangladesh-garment-factory-36437966/",
    },
  },
  {
    id: "weaving-floor",
    src: "/site/hero/weaving-floor.jpg",
    alt: "A long aisle between rows of modern green weaving machines with Bangla safety signs, in a factory in Narayanganj, Bangladesh",
    width: 1800,
    height: 1200,
    focus: "50% 50%",
    caption: "Weaving floor, Narayanganj",
    credit: {
      name: "Shamsuddin Habib",
      url: "https://www.pexels.com/photo/people-working-in-factory-18293962/",
      source: "Pexels",
      sourceUrl: "https://www.pexels.com/photo/people-working-in-factory-18293962/",
    },
  },
  {
    id: "machine-operator",
    src: "/site/hero/machine-operator.jpg",
    alt: "A young man operating a row of thread-winding machines on a clean, modern textile factory floor in Bangladesh",
    width: 1800,
    height: 1200,
    focus: "45% 50%",
    caption: "Winding section, Bangladesh",
    credit: {
      name: "Galib Rahman Nadim",
      url: "https://www.pexels.com/@capturedbynadim/",
      source: "Pexels",
      sourceUrl: "https://www.pexels.com/photo/worker-operating-textile-machinery-in-factory-39351273/",
    },
  },
];

/** Shown when no setting is stored, the API can't be reached, or a stored id no longer exists. */
export const HERO_DEFAULT_ID = "factory-floor-team";

export interface HeroSettings {
  /** Photos to show, in order. The first one is shown first. */
  imageIds: string[];
  /** Crossfade between the selected photos. Off by default. */
  rotate: boolean;
  /** Seconds each photo stays when rotating (2 to 30). */
  intervalSeconds: number;
}

export const HERO_DEFAULTS: HeroSettings = { imageIds: [HERO_DEFAULT_ID], rotate: false, intervalSeconds: 9 };

export const heroImageById = (id: string) => HERO_IMAGES.find((i) => i.id === id);

/** Resolve stored settings to real images, dropping unknown ids; never returns an empty list. */
export function resolveHero(settings: Partial<HeroSettings> | null | undefined): { images: HeroImage[]; rotate: boolean; intervalSeconds: number } {
  const ids = settings?.imageIds?.length ? settings.imageIds : HERO_DEFAULTS.imageIds;
  const images = ids.map(heroImageById).filter((x): x is HeroImage => Boolean(x));
  const safe = images.length ? images : [heroImageById(HERO_DEFAULT_ID)!];
  const interval = Math.min(30, Math.max(2, Math.round(settings?.intervalSeconds ?? HERO_DEFAULTS.intervalSeconds)));
  return { images: safe, rotate: Boolean(settings?.rotate) && safe.length > 1, intervalSeconds: interval };
}
