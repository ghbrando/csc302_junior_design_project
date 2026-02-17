// Components/AuthorizedPageBase.cs
using Microsoft.AspNetCore.Components;

namespace unicoreprovider.Services;

public class AuthorizedPageBase : ComponentBase
{
    [Inject] protected IAuthStateService AuthState { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        await Task.Yield(); // Ensure this runs after the first render
        if (string.IsNullOrEmpty(AuthState.FirebaseUid))
        {
            Nav.NavigateTo("/", forceLoad: false);
        }
    }
}