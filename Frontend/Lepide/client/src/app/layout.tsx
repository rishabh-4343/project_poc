import type { Metadata } from "next";
import { Geist, Geist_Mono } from "next/font/google";
import "./globals.css";
import { Toaster } from "sonner";
import QueryProvider from "@/providers/query-provider";
import { AppShell } from "@/shared/ui/app-shell";
import { BRAND_NAME } from "@/shared/brand";

const geistSans = Geist({
  variable: "--font-geist-sans",
  subsets: ["latin"],
});

const geistMono = Geist_Mono({
  variable: "--font-geist-mono",
  subsets: ["latin"],
});

export const metadata: Metadata = {
  title: { default: BRAND_NAME, template: `%s · ${BRAND_NAME}` },
  description: "Data security auditing",
};

// Applies the saved (or system) theme before first paint so there is no light flash in dark mode.
const themeScript = `try{var t=localStorage.getItem('theme');if(t==='dark'||(!t&&matchMedia('(prefers-color-scheme: dark)').matches))document.documentElement.classList.add('dark')}catch(e){}`;

export default function RootLayout({ children }: LayoutProps<"/">) {
  return (
    <html
      lang="en"
      suppressHydrationWarning
      className={`${geistSans.variable} ${geistMono.variable} h-full antialiased`}
    >
      <head>
        <script dangerouslySetInnerHTML={{ __html: themeScript }} />
      </head>
      <body className="min-h-full">
        <QueryProvider>
          <AppShell>{children}</AppShell>
          <Toaster
            position="bottom-right"
            style={{ "--normal-bg": "var(--surface)", "--normal-text": "var(--fg)", "--normal-border": "var(--border)" } as React.CSSProperties}
          />
        </QueryProvider>
      </body>
    </html>
  );
}
