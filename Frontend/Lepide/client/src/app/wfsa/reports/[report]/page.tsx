import { Suspense } from "react";
import { ReportPage, ReportPageSkeleton } from "@/features/wfsa";

// The slug is request-time data under Cache Components, so it is read inside Suspense and the shell stays static.
async function ReportLoader({ params }: { params: PageProps<"/wfsa/reports/[report]">["params"] }) {
  const { report } = await params;
  return <ReportPage slug={report} />;
}

export default function Page(props: PageProps<"/wfsa/reports/[report]">) {
  return (
    <Suspense fallback={<ReportPageSkeleton />}>
      <ReportLoader params={props.params} />
    </Suspense>
  );
}
