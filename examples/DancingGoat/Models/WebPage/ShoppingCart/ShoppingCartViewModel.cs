namespace DancingGoat.Models;

public record ShoppingCartViewModel(ICollection<ShoppingCartItemViewModel> Items, decimal TotalPrice, decimal SubtotalPrice, decimal TotalTax, decimal TotalDiscount);
