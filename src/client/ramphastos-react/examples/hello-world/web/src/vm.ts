/** Client-side contract for the server's CounterVm. */
export interface CounterVm {
  count: number;
  title: string;
  items: string[];
  increment(by?: number): Promise<unknown>;
  decrement(): Promise<unknown>;
  echo(text: string): Promise<unknown>;
  addItem(text: string): Promise<unknown>;
  clearItems(): Promise<unknown>;
}
