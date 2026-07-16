let article;

function scrollToFragment(fragment) {
    const id = fragment.startsWith("#") ? fragment.substring(1) : fragment;
    if (!/^help-section-\d+$/.test(id)) {
        return;
    }

    document.getElementById(id)?.scrollIntoView({ block: "start" });
}

function onHelpLinkClick(event) {
    if (!(event.target instanceof Element)) {
        return;
    }

    const link = event.target.closest('.help-article a[href^="/help#help-section-"]');
    if (link) {
        scrollToFragment(link.hash);
    }
}

export function initializeHelpNavigation() {
    disposeHelpNavigation();
    article = document.querySelector(".help-article");
    article?.addEventListener("click", onHelpLinkClick);
    window.addEventListener("hashchange", scrollToCurrentFragment);
    window.addEventListener("popstate", scrollToCurrentFragment);
    scrollToCurrentFragment();
}

export function scrollToCurrentFragment() {
    scrollToFragment(window.location.hash);
}

export function disposeHelpNavigation() {
    article?.removeEventListener("click", onHelpLinkClick);
    article = undefined;
    window.removeEventListener("hashchange", scrollToCurrentFragment);
    window.removeEventListener("popstate", scrollToCurrentFragment);
}
