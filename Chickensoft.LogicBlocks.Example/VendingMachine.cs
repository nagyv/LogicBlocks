namespace Chickensoft.LogicBlocks.Example;

using Chickensoft.LogicBlocks.Generator;

[StateMachine]
public partial class VendingMachine {
  // Inputs

  public abstract record Input {
    public record SelectionEntered(ItemType Type) : Input;
    public record PaymentReceived(int Amount) : Input;
    public record TransactionTimedOut : Input;
    public record VendingCompleted : Input;
  }

  public abstract record State(Context Context) : StateLogic(Context) {
    public record Idle : State,
      IGet<Input.SelectionEntered>, IGet<Input.PaymentReceived> {
      public Idle(Context context) : base(context) {
        context.OnEnter<Idle>((previous) => context.Output(
          new Output.ClearTransactionTimeOutTimer()
        ));
      }

      public State On(Input.SelectionEntered input) {
        if (Context.Get<VendingMachineStock>().HasItem(input.Type)) {
          return new TransactionActive.Started(
            Context, input.Type, Prices[input.Type], 0
          );
        }
        return this;
      }

      public State On(Input.PaymentReceived input) {
        // Money was deposited with no selection — eject it right back.
        //
        // We could be evil and keep it, but we'd ruin our reputation as a
        // reliable vending machine in the office and then we'd never get ANY
        // money!
        Context.Output(new Output.MakeChange(input.Amount));
        return this;
      }
    }

    public abstract record TransactionActive : State,
      IGet<Input.PaymentReceived>, IGet<Input.TransactionTimedOut> {
      public ItemType Type { get; }
      public int Price { get; }
      public int AmountReceived { get; }

      public TransactionActive(
        Context context, ItemType type, int price, int amountReceived
      ) : base(context) {
        Type = type;
        Price = price;
        AmountReceived = amountReceived;

        Context.OnEnter<TransactionActive>(
         (previous) => Context.Output(
           new Output.RestartTransactionTimeOutTimer()
         )
       );
      }

      public State On(Input.PaymentReceived input) {
        var total = AmountReceived + input.Amount;

        if (total >= Price) {
          if (total > Price) {
            Context.Output(new Output.MakeChange(total - Price));
          }
          Context.Output(
            new Output.TransactionCompleted(
              Type: Type,
              Price: Price,
              Status: TransactionStatus.Success,
              AmountPaid: total
            )
          );
          Context.Get<VendingMachineStock>().Vend(Type);
          return new Vending(Context, Type, Price);
        }

        return new PaymentPending(Context, Type, Price, total);
      }

      public State On(Input.TransactionTimedOut input) {
        if (AmountReceived > 0) {
          // Give any money received back before timing out.
          Context.Output(new Output.MakeChange(AmountReceived));
        }
        return new Idle(Context);
      }

      public record Started : TransactionActive,
        IGet<Input.SelectionEntered> {
        public Started(
          Context context, ItemType type, int price, int amountReceived
        ) : base(context, type, price, amountReceived) {
          context.OnEnter<Started>(
            (previous) => context.Output(new Output.TransactionStarted())
          );
        }

        // While in this state, user can change selection as much as they want.
        public State On(Input.SelectionEntered input) {
          if (Context.Get<VendingMachineStock>().HasItem(input.Type)) {
            return new Started(
              Context, input.Type, Prices[input.Type], AmountReceived
            );
          }
          // Item not in stock — clear selection.
          return new Idle(Context);
        }
      }

      public record PaymentPending(
        Context Context, ItemType Type, int Price, int AmountReceived
      ) : TransactionActive(Context, Type, Price, AmountReceived);
    }

    public record Vending : State, IGet<Input.VendingCompleted> {
      public ItemType Type { get; }
      public int Price { get; }

      public Vending(Context context, ItemType type, int price) :
        base(context) {
        Type = type;
        Price = price;

        context.OnEnter<Vending>(
          (previous) => Context.Output(new Output.BeginVending())
        );
      }

      public State On(Input.VendingCompleted input) =>
        new Idle(Context);
    }
  }

  // Side effects

  public abstract record Output {
    public record Dispensed(ItemType Type) : Output;
    public record TransactionStarted : Output;
    public record TransactionCompleted(
      ItemType Type, int Price, TransactionStatus Status, int AmountPaid
    ) : Output;
    public record RestartTransactionTimeOutTimer : Output;
    public record ClearTransactionTimeOutTimer : Output;
    public record MakeChange(int Amount) : Output;
    public record BeginVending : Output { }
  }

  // Feature-specific stuff

  public static readonly Dictionary<ItemType, int> Prices = new() {
    [ItemType.Juice] = 4,
    [ItemType.Water] = 2,
    [ItemType.Candy] = 6
  };
}

// Logic Block / Hierarchical State Machine

public partial class VendingMachine :
  LogicBlock<
    VendingMachine.Input, VendingMachine.State, VendingMachine.Output
  > {
  public VendingMachine(VendingMachineStock stock) {
    Set(stock);
  }

  public override State GetInitialState(Context context)
    => new State.Idle(context);
}

// Just a domain layer repository that manages the stock for a vending machine.
public class VendingMachineStock {
  public Dictionary<ItemType, int> Stock { get; }

  public VendingMachineStock(Dictionary<ItemType, int> stock) {
    Stock = stock;
  }

  public int Qty(ItemType type) => Stock[type];
  public bool HasItem(ItemType type) => Stock[type] > 0;
  public void Vend(ItemType type) => Stock[type]--;
}
