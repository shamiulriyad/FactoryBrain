"use client";

// Live monitoring updates over SignalR (/hubs/monitoring).
//
// - Authenticated: the access token comes from the session (refreshed when
//   it's about to expire); negotiate is refused (401/403) without it.
// - Scoped: after connecting, the page subscribes to one factory (or all of
//   the user's); the server decides which groups that means, from the token.
// - Resilient: automatic reconnect with back-off; after a reconnect the page
//   subscribes again and refetches, so nothing missed while offline is lost.
//   When the server closes the connection (token expired, restart) a new one
//   is started with a fresh token.
// - Clean: the connection is stopped when the page unmounts or the factory
//   changes.

import { useEffect, useRef, useState } from "react";
import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from "@microsoft/signalr";
import { getAccessToken } from "@/lib/auth";
import type { MonitoringAlert, MonitoringEvent } from "@/lib/monitoring";
import type { IdentityAssociation } from "@/lib/identity";

export type LiveState = "connecting" | "live" | "reconnecting" | "offline" | "denied" | "off";

export type StatusMessage = { id: string; code: string; status: string | null; sourceType: string; occurredAt: string; eventId: string };

export type HubHandlers = {
  onEvent?: (e: MonitoringEvent) => void;
  onAlert?: (change: string, alert: MonitoringAlert) => void;
  onCameraStatus?: (m: StatusMessage) => void;
  onDeviceStatus?: (m: StatusMessage) => void;
  /** A worker-identity association changed (created, refreshed, suspended, expired, reviewed). */
  onIdentity?: (change: string, association: IdentityAssociation) => void;
  /** After (re)connecting: refetch to fill any gap. */
  onResync?: () => void;
};

export type Subscription = { allFactories: boolean; factories: string[]; lines: string[]; categories: string[]; subscribedAt: string };

const RETRY_DELAYS = [0, 2_000, 5_000, 10_000, 20_000, 30_000];
const RESTART_AFTER_MS = 15_000;

export function useMonitoringHub(factoryId: string | null | undefined, handlers: HubHandlers, enabled = true) {
  const [state, setState] = useState<LiveState>(enabled ? "connecting" : "off");
  const [subscription, setSubscription] = useState<Subscription | null>(null);
  const [lastMessageAt, setLastMessageAt] = useState<Date | null>(null);
  const [error, setError] = useState<string | null>(null);
  const handlersRef = useRef(handlers);
  handlersRef.current = handlers;

  useEffect(() => {
    if (!enabled) {
      setState("off");
      return;
    }
    let disposed = false;
    let timer: ReturnType<typeof setTimeout> | null = null;
    let conn: HubConnection | null = null;

    const touch = () => setLastMessageAt(new Date());

    async function subscribe(c: HubConnection) {
      const sub = await c.invoke<Subscription>("Subscribe", factoryId || null);
      if (disposed) return;
      setSubscription(sub);
      setState("live");
      setError(null);
    }

    function build(): HubConnection {
      const c = new HubConnectionBuilder()
        .withUrl("/hubs/monitoring", { accessTokenFactory: getAccessToken })
        .withAutomaticReconnect(RETRY_DELAYS)
        .configureLogging(LogLevel.None)
        .build();
      c.on("monitoringEvent", (e: MonitoringEvent) => { touch(); handlersRef.current.onEvent?.(e); });
      c.on("monitoringAlert", (m: { change: string; alert: MonitoringAlert }) => { touch(); handlersRef.current.onAlert?.(m.change, m.alert); });
      c.on("cameraStatus", (m: StatusMessage) => { touch(); handlersRef.current.onCameraStatus?.(m); });
      c.on("deviceStatus", (m: StatusMessage) => { touch(); handlersRef.current.onDeviceStatus?.(m); });
      c.on("identityAssociation", (m: { change: string; association: IdentityAssociation }) => { touch(); handlersRef.current.onIdentity?.(m.change, m.association); });
      c.onreconnecting(() => { if (!disposed) setState("reconnecting"); });
      c.onreconnected(async () => {
        if (disposed) return;
        try {
          await subscribe(c); // a reconnect is a new connection: its groups are gone
          handlersRef.current.onResync?.();
        } catch (e) {
          setError(e instanceof Error ? e.message : "Could not subscribe.");
          setState("offline");
        }
      });
      c.onclose(() => {
        if (disposed) return;
        setState("offline");
        timer = setTimeout(start, RESTART_AFTER_MS);
      });
      return c;
    }

    async function start() {
      if (disposed) return;
      setState((s) => (s === "live" ? "reconnecting" : "connecting"));
      conn = build();
      try {
        await conn.start();
        if (disposed) { await conn.stop(); return; }
        await subscribe(conn);
        handlersRef.current.onResync?.();
      } catch (e) {
        if (disposed) return;
        const message = e instanceof Error ? e.message : String(e);
        if (/\b40[13]\b|Unauthorized|Forbidden/i.test(message) || /monitoring_access_required|factory_out_of_scope/.test(message)) {
          setState("denied");
          setError(message);
          await conn?.stop().catch(() => undefined);
          return;
        }
        setError(message);
        setState("offline");
        await conn?.stop().catch(() => undefined);
        timer = setTimeout(start, RESTART_AFTER_MS);
      }
    }

    void start();
    return () => {
      disposed = true;
      if (timer) clearTimeout(timer);
      if (conn && conn.state !== HubConnectionState.Disconnected) void conn.stop().catch(() => undefined);
    };
  }, [factoryId, enabled]);

  return { state, subscription, lastMessageAt, error };
}
