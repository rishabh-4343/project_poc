import type { Metadata } from "next";
import { NewSessionPage } from "@/features/wfsa";

export const metadata: Metadata = { title: "New audit" };

export default function Page() {
  return <NewSessionPage />;
}
