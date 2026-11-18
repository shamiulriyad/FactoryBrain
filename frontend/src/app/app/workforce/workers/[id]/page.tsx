import { redirect } from "next/navigation";

// Worker profiles live in the worker directory; old Workforce links land there.
export default async function WorkforceWorkerRedirect({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  redirect(`/app/workers/${encodeURIComponent(id)}`);
}
