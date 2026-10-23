// Centralised seed loader — builds the dataset once and exposes it to all services.
import { buildProducts } from "@/data/products";
import { buildCustomers } from "@/data/customers";
import { buildOrders } from "@/data/orders";
import { suppliers } from "@/data/suppliers";
import { buildInventory } from "@/data/inventory";
import { policies } from "@/data/policies";
import { conversations } from "@/data/conversations";
import { goals } from "@/data/goals";
import { buildGraph } from "@/data/graph";
import { buildInsights } from "@/data/insights";
import { buildActivity } from "@/data/activity";
import { agents } from "@/data/agents";
import { computeHealth } from "@/data/analytics";

const products = buildProducts(342);
const customers = buildCustomers(4218);
const { orders, byDay } = buildOrders(customers, products);
const inventory = buildInventory(products, orders);
const health = computeHealth(orders, byDay, customers, inventory);
const insights = buildInsights(health, inventory, products, customers, orders);
const activity = buildActivity();

// Graph is lazy because buildGraph() touches the client-side factory store.
// Server routes that never read `dataset.graph` should not pay for the import.
let _graph: ReturnType<typeof buildGraph> | null = null;
function lazyGraph() {
  if (!_graph) _graph = buildGraph(products, customers, suppliers, orders, goals);
  return _graph;
}

export const dataset = {
  products,
  customers,
  orders,
  byDay,
  inventory,
  suppliers,
  policies,
  conversations,
  goals,
  agents,
  health,
  insights,
  get graph() {
    return lazyGraph();
  },
  activity
};