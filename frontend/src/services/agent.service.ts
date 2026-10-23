import { dataset } from "./dataset";
import type { AgentPublic } from "./types";
import { AGENT_GLYPH, type AgentGlyph } from "@/components/agents/AgentAvatar";

export const agentService = {
  list(): AgentPublic[] {
    return dataset.agents.map((a) => {
      const tasks = dataset.insights.filter(
        (i) => i.agentId === a.id && (i.stage === "suggested" || i.stage === "pending_approval" || i.stage === "executing")
      ).length;
      const recent = dataset.insights.filter(
        (i) => i.agentId === a.id && (i.stage === "done" || i.stage === "logged")
      ).length;
      return {
        id: a.id,
        name: a.name,
        nameBn: a.nameBn,
        purpose: a.purpose,
        purposeBn: a.purposeBn,
        risk: a.risk as AgentPublic["risk"],
        execution: a.execution,
        model: a.model,
        contextSlices: a.contextSlices,
        status: a.status,
        tasksToday: tasks,
        recentCount: recent,
        glyph: (AGENT_GLYPH[a.id] ?? "orchestrator") as AgentGlyph
      };
    });
  },
  get(id: string): AgentPublic | undefined {
    return this.list().find((a) => a.id === id);
  }
};