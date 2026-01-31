declare module "cs2/bindings" {
  export interface Entity {
  	index: number;
  	version: number;
  }
  export interface ValueBinding<T> {
  	readonly value: T;
  	subscribe(listener?: BindingListener<T>): ValueSubscription<T>;
  	dispose(): void;
  }
  export interface MapBinding<K, V> {
  	getValue(key: K): V;
  	subscribe(key: K, listener?: BindingListener<V>): ValueSubscription<V>;
  	dispose(): void;
  }
  export interface EventBinding<T> {
  	subscribe(listener: BindingListener<T>): Subscription;
  	dispose(): void;
  }
  export interface BindingListener<T> {
  	(value: T): void;
  }
  export interface Subscription {
  	dispose(): void;
  }
  export interface ValueSubscription<T> extends Subscription {
  	readonly value: T;
  	setChangeListener(listener: BindingListener<T>): void;
  }
  const focusedEntity$: ValueBinding<Entity>;
  function focusEntity(entity: Entity): void;

  export {};
}
