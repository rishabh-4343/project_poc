import type { Metadata } from "next";
import { ServersPage } from "@/features/wfsa";

export const metadata: Metadata = { title: "Servers" };

export default function Page() {
  return <ServersPage />;
}
