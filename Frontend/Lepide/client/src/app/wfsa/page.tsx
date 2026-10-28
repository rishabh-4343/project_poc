import type { Metadata } from "next";
import { DashboardPage } from "@/features/wfsa";

export const metadata: Metadata = { title: "Dashboard" };

export default function Page() {
  return <DashboardPage />;
}
