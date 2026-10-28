// Module nav registry. Each module adds exactly one entry here (the composition point).
import { FileBarChart, FolderTree, LayoutDashboard, Plug, Radio, Server, type LucideIcon } from "lucide-react";
import { REPORT_TREE, isGroup, reportHref } from "@/features/wfsa/reports";

export type NavItem = {
  label: string;
  href: string;
  icon?: LucideIcon;
  exact?: boolean;
  // A collapsible branch; its own href is the branch's overview page.
  children?: NavItem[];
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
      { label: "Audits", href: "/wfsa/sessions", icon: Radio },
      {
        label: "Reports",
        href: "/wfsa/reports",
        icon: FileBarChart,
        exact: true,
        children: REPORT_TREE.map((node) =>
          isGroup(node)
            ? { label: node.group, href: `/wfsa/reports#${node.slug}`, children: node.reports.map((r) => ({ label: r.title, href: reportHref(r.slug) })) }
            : { label: node.title, href: reportHref(node.slug) },
        ),
      },
      { label: "Servers", href: "/wfsa/servers", icon: Server },
      { label: "Shares", href: "/wfsa/shares", icon: FolderTree },
      { label: "Agents", href: "/wfsa/agents", icon: Plug },
    ],
  },
];

// Every link in the tree, for labels such as breadcrumbs.
export function flattenNav(items: NavItem[] = modules.flatMap((m) => m.children ?? [])): NavItem[] {
  return items.flatMap((i) => [i, ...flattenNav(i.children ?? [])]);
}
