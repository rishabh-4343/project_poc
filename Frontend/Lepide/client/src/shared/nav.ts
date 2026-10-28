// Module nav registry. Each module adds exactly one entry here (the composition point).
import { FolderTree, LayoutDashboard, Plug, Radio, Server, type LucideIcon } from "lucide-react";

export type NavItem = {
  label: string;
  href: string;
  icon?: LucideIcon;
  exact?: boolean;
};

export type NavModule = {
  slug: string;
  label: string;
  href: string;
  children?: NavItem[];
};

export const modules: NavModule[] = [
  {
    slug: "wfsa",
    label: "File Server Auditing",
    href: "/wfsa",
    children: [
      { label: "Dashboard", href: "/wfsa", icon: LayoutDashboard, exact: true },
      { label: "Agents", href: "/wfsa/agents", icon: Plug },
      { label: "Servers", href: "/wfsa/servers", icon: Server },
      { label: "Shares", href: "/wfsa/shares", icon: FolderTree },
      { label: "Sessions", href: "/wfsa/sessions", icon: Radio },
    ],
  },
];
