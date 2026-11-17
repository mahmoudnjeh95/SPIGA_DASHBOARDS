using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Spiga.UI.Services;

namespace Spiga.UI.Components;

// Wraps the application and reports every click and key press to the
// session (inactivity lock) without re-rendering anything.
public sealed class ActivityZone : ComponentBase, IHandleEvent
{
    [Inject] private UserSession Session { get; set; } = default!;
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public string Class { get; set; } = "app";

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", Class);
        builder.AddAttribute(2, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, Session.Touch));
        builder.AddAttribute(3, "onkeydown", EventCallback.Factory.Create<KeyboardEventArgs>(this, Session.Touch));
        builder.AddContent(4, ChildContent);
        builder.CloseElement();
    }

    Task IHandleEvent.HandleEventAsync(EventCallbackWorkItem callback, object? arg) => callback.InvokeAsync(arg);
}
