import type { ReportActionFilter } from "./types";

// Predefined reports: each is a fixed filter over everything the agents captured, on every audited server.
// "actions" lists individual events; "reads" lists file reads, which the agent merges to one row per user, file and hour.
export type ReportSpec = {
  slug: string;
  title: string;
  description: string;
  actions?: ReportActionFilter;
  reads?: { success: boolean };
};

export type ReportGroup = { group: string; slug: string; reports: ReportSpec[] };

const CHANGES = ["Created", "Modified", "Deleted", "Renamed", "Moved", "Copied", "PropertyChanged", "PermissionChange", "OwnerChange", "AuditingChange"];
const READS = ["Read", "ContentView", "ReadBasicInfo"];
const SECURITY = ["PermissionChange", "OwnerChange", "AuditingChange"];
const FILES_AND_FOLDERS = ["File", "Folder"];

export const REPORT_TREE: (ReportSpec | ReportGroup)[] = [
  {
    slug: "all-modifications",
    title: "All Modifications in File Server",
    description: "Every change to files and folders on all audited servers, allowed or denied.",
    actions: { operations: CHANGES, objectTypes: FILES_AND_FOLDERS },
  },
  {
    slug: "shared-modifications",
    title: "All Modifications in Shared File and Folder",
    description: "Changes to files and folders inside registered shares.",
    actions: { operations: CHANGES, objectTypes: FILES_AND_FOLDERS, scope: "shares" },
  },
  {
    slug: "long-path-modifications",
    title: "All Modifications in Long File Path",
    description: "Changes to items whose path is longer than 260 characters, which many Windows tools cannot open.",
    actions: { operations: CHANGES, objectTypes: FILES_AND_FOLDERS, scope: "longPaths" },
  },
  {
    slug: "file-copied",
    title: "File Copied",
    description: "Files copied within or between audited folders.",
    actions: { operations: ["Copied"] },
  },
  {
    slug: "read-successful",
    title: "Read Successful",
    description: "Files and folders that were opened and read.",
    actions: { operations: READS, success: true },
    reads: { success: true },
  },
  {
    slug: "read-failed",
    title: "Read Failed",
    description: "Attempts to read files and folders that were denied.",
    actions: { operations: READS, success: false },
    reads: { success: false },
  },
  {
    slug: "created",
    title: "File and Folder Created",
    description: "New files and folders, including attempts that were denied.",
    actions: { operations: ["Created"], objectTypes: FILES_AND_FOLDERS },
  },
  {
    slug: "creation-failed",
    title: "File and Folder Creation Failed",
    description: "Attempts to create files and folders that were denied.",
    actions: { operations: ["Created"], objectTypes: FILES_AND_FOLDERS, success: false },
  },
  {
    slug: "deleted",
    title: "File and Folder Deleted",
    description: "Files and folders that were deleted, including attempts that were denied.",
    actions: { operations: ["Deleted"], objectTypes: FILES_AND_FOLDERS },
  },
  {
    slug: "renamed",
    title: "File and Folder Renamed",
    description: "Files and folders that were renamed, with the old and new name.",
    actions: { operations: ["Renamed"], objectTypes: FILES_AND_FOLDERS },
  },
  {
    slug: "moved",
    title: "File and Folder Moved",
    description: "Files and folders moved to another folder.",
    actions: { operations: ["Moved"], objectTypes: FILES_AND_FOLDERS },
  },
  {
    group: "Permission Reports",
    slug: "permissions",
    reports: [
      {
        slug: "all-permission-modifications",
        title: "All Permission Modifications",
        description: "Changes to permissions, ownership and auditing settings on files and folders.",
        actions: { operations: SECURITY },
      },
      {
        slug: "file-permissions-modified",
        title: "File Permissions Modified",
        description: "Changes to the permissions of files.",
        actions: { operations: ["PermissionChange"], objectTypes: ["File"] },
      },
      {
        slug: "folder-permissions-modified",
        title: "Folder Permissions Modified",
        description: "Changes to the permissions of folders.",
        actions: { operations: ["PermissionChange"], objectTypes: ["Folder"] },
      },
    ],
  },
];

export const isGroup = (node: ReportSpec | ReportGroup): node is ReportGroup => "group" in node;

export const REPORTS: ReportSpec[] = REPORT_TREE.flatMap((n) => (isGroup(n) ? n.reports : [n]));

export const reportHref = (slug: string) => `/wfsa/reports/${slug}`;

export const findReport = (slug: string) => REPORTS.find((r) => r.slug === slug);
