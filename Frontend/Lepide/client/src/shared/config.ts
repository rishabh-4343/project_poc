const base = process.env.NEXT_PUBLIC_API_URL;

if (!base) {
  throw new Error("NEXT_PUBLIC_API_URL is not set. Add it to .env.local (see .env.example).");
}

export const API_URL = base.replace(/\/+$/, "");
