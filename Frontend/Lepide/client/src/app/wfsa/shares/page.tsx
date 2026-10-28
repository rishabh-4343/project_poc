import type { Metadata } from "next";
import { SharesPage } from "@/features/wfsa";

export const metadata: Metadata = { title: "Shares" };

export default function Page() {
  return <SharesPage />;
}
