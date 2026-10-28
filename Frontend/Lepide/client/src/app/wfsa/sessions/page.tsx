import type { Metadata } from "next";
import { SessionsPage } from "@/features/wfsa";

export const metadata: Metadata = { title: "Sessions" };

export default function Page() {
  return <SessionsPage />;
}
