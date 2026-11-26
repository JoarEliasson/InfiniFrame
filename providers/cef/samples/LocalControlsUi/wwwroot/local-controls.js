// Browser primitives only: in-page dialogs, focus and captured pointer gestures.
// No state storage, query-string lifecycle simulation, network or CSSOM stylesheet mutation.
export function createDialogs(parent, confirmation, dotnet) {
    const handle = parent.querySelector('[data-drag-handle]');
    let disposed = false;
    let drag = null;
    let returnFocus = null;
    let generation = 0;

    function endDrag(event) {
        if (!drag || (event && event.pointerId !== drag.id)) return;
        const id = drag.id;
        drag = null;
        if (handle.hasPointerCapture(id)) handle.releasePointerCapture(id);
    }
    function pointerDown(event) {
        if (disposed || event.button !== 0 || confirmation.open || event.target.closest('button')) return;
        const rect = parent.getBoundingClientRect();
        drag = { id: event.pointerId, x: event.clientX - rect.left, y: event.clientY - rect.top };
        handle.setPointerCapture(event.pointerId);
        event.preventDefault();
    }
    function pointerMove(event) {
        if (!drag || event.pointerId !== drag.id) return;
        const x = Math.max(0, Math.min(innerWidth - parent.offsetWidth, event.clientX - drag.x));
        const y = Math.max(0, Math.min(innerHeight - parent.offsetHeight, event.clientY - drag.y));
        // Deliberately use a style attribute; never insertRule/replaceSync or a stylesheet shim.
        parent.setAttribute('style', `left:${x}px;top:${y}px;transform:none`);
    }
    function parentClosed() {
        endDrag();
        if (disposed || parent.open) return;
        if (confirmation.open) confirmation.close();
        if (returnFocus?.isConnected) returnFocus.focus();
        // Report unexpected live-document callback failures. Disposal can retire a callback in flight.
        dotnet.invokeMethodAsync('ParentClosedAsync', generation).catch(error => {
            if (!disposed) console.error('LocalControlsUi close callback failed', error);
        });
    }

    handle.addEventListener('pointerdown', pointerDown);
    handle.addEventListener('pointermove', pointerMove);
    handle.addEventListener('pointerup', endDrag);
    handle.addEventListener('pointercancel', endDrag);
    handle.addEventListener('lostpointercapture', endDrag);
    parent.addEventListener('close', parentClosed);

    return {
        showParent(nextGeneration) {
            if (disposed) return 0;
            if (!parent.open) {
                returnFocus = document.activeElement;
                parent.removeAttribute('style');
                parent.showModal();
            }
            // A retry adopts the same live dialog, with a close callback matching its acknowledgement.
            generation = nextGeneration;
            return generation;
        },
        closeParent() {
            if (disposed) return;
            endDrag();
            if (confirmation.open) confirmation.close();
            if (parent.open) parent.close();
        },
        showConfirmation() {
            if (!disposed && parent.open && !confirmation.open) {
                endDrag();
                confirmation.showModal();
            }
        },
        closeConfirmation() {
            if (!disposed && confirmation.open) confirmation.close();
        },
        dispose() {
            if (disposed) return;
            disposed = true;
            endDrag();
            handle.removeEventListener('pointerdown', pointerDown);
            handle.removeEventListener('pointermove', pointerMove);
            handle.removeEventListener('pointerup', endDrag);
            handle.removeEventListener('pointercancel', endDrag);
            handle.removeEventListener('lostpointercapture', endDrag);
            parent.removeEventListener('close', parentClosed);
            if (confirmation.open) confirmation.close();
            if (parent.open) parent.close();
            returnFocus = null;
        }
    };
}
