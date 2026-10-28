import { Suspense } from "react";
import { SessionPage, SessionPageSkeleton } from "@/features/wfsa";

// The id is request-time data under Cache Components, so it is read inside Suspense and the shell stays static.
async function SessionLoader({ params }: { params: PageProps<"/wfsa/sessions/[id]">["params"] }) {
  const { id } = await params;
  return <SessionPage id={id} />;
}

export default function Page(props: PageProps<"/wfsa/sessions/[id]">) {
  return (
    <Suspense fallback={<SessionPageSkeleton />}>
      <SessionLoader params={props.params} />
    </Suspense>
  );
}
