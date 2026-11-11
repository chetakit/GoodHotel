// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

(() => {
    const modal = document.getElementById("room-calendar-modal");
    if (!modal) {
        return;
    }

    const closeButtons = modal.querySelectorAll("[data-calendar-close]");
    const roomDetails = document.getElementById("calendar-room-details");
    const monthLabel = document.getElementById("calendar-month-label");
    const daysContainer = document.getElementById("calendar-days");
    const prompt = document.getElementById("calendar-selection-prompt");
    const errorBox = document.getElementById("calendar-error");
    const guestsInput = document.getElementById("calendar-guests");
    const nightCount = document.getElementById("calendar-night-count");
    const total = document.getElementById("calendar-total");
    const continueButton = document.getElementById("calendar-continue");
    const previousButton = document.getElementById("calendar-previous-month");
    const nextButton = document.getElementById("calendar-next-month");
    const monthFormatter = new Intl.DateTimeFormat("th-TH", {
        month: "long",
        timeZone: "UTC"
    });
    const moneyFormatter = new Intl.NumberFormat("th-TH", {
        style: "currency",
        currency: "THB",
        maximumFractionDigits: 2
    });
    const state = {
        roomId: null,
        roomNumber: "",
        roomType: "",
        rate: null,
        capacity: null,
        roomCanBeBooked: false,
        today: "",
        monthYear: 0,
        monthNumber: 0,
        checkIn: "",
        checkOut: "",
        months: new Map(),
        lastFocusedElement: null,
        renderVersion: 0
    };

    const parseIsoDate = value => {
        const [year, month, day] = value.split("-").map(Number);
        return new Date(Date.UTC(year, month - 1, day));
    };

    const formatIsoDate = date => [
        date.getUTCFullYear().toString().padStart(4, "0"),
        (date.getUTCMonth() + 1).toString().padStart(2, "0"),
        date.getUTCDate().toString().padStart(2, "0")
    ].join("-");

    const monthKey = (year, month) => `${year}-${month}`;

    const setError = message => {
        errorBox.textContent = message;
        errorBox.hidden = !message;
    };

    const showPrompt = message => {
        prompt.textContent = message;
    };

    const getCachedPeriods = () => [...state.months.values()]
        .flatMap(month => month.bookingPeriods);

    const rangeHasConflict = (checkIn, checkOut) =>
        getCachedPeriods().some(period =>
            period.checkIn < checkOut &&
            period.checkOut > checkIn);

    const loadMonth = async (year, month) => {
        const key = monthKey(year, month);
        if (state.months.has(key)) {
            return state.months.get(key);
        }

        const url = new URL(modal.dataset.availabilityUrl, window.location.origin);
        url.searchParams.set("roomId", state.roomId);
        url.searchParams.set("year", year);
        url.searchParams.set("month", month);

        const response = await fetch(url, {
            headers: { Accept: "application/json" }
        });
        const result = await response.json();
        if (!response.ok) {
            throw new Error(result.message || "ไม่สามารถโหลดข้อมูลวันว่างได้");
        }

        const data = {
            ...result,
            bookingPeriods: (result.bookingPeriods || []).map(period => ({
                checkIn: period.checkIn,
                checkOut: period.checkOut
            }))
        };
        state.months.set(key, data);
        state.today = result.today;
        state.rate = result.rate;
        state.capacity = result.capacity;
        state.roomCanBeBooked =
            result.status === "Available" &&
            Number.isFinite(result.rate) &&
            result.rate > 0 &&
            Number.isInteger(result.capacity) &&
            result.capacity > 0;
        roomDetails.textContent =
            `ห้อง ${result.roomNumber || "-"} · ${result.roomType || "Room"} · ` +
            (state.rate > 0 ? `${moneyFormatter.format(state.rate)} / คืน` : "ไม่พบราคา");

        if (Number.isInteger(state.capacity) && state.capacity > 0) {
            guestsInput.max = state.capacity.toString();
        } else {
            guestsInput.removeAttribute("max");
        }

        if (result.status !== "Available") {
            setError("ห้องนี้ไม่พร้อมให้จอง กรุณาเลือกห้องอื่น");
        } else if (!state.roomCanBeBooked) {
            setError("ข้อมูลราคา หรือจำนวนผู้เข้าพักของห้องนี้ยังไม่ครบถ้วน");
        }

        return data;
    };

    const ensureMonthsInRange = async (fromIso, toIso) => {
        const from = parseIsoDate(fromIso);
        const to = parseIsoDate(toIso);
        const pending = [];
        let year = from.getUTCFullYear();
        let month = from.getUTCMonth() + 1;
        const endYear = to.getUTCFullYear();
        const endMonth = to.getUTCMonth() + 1;

        while (year < endYear || (year === endYear && month <= endMonth)) {
            pending.push(loadMonth(year, month));
            month += 1;
            if (month > 12) {
                month = 1;
                year += 1;
            }
        }

        await Promise.all(pending);
    };

    const renderCalendar = data => {
        const year = state.monthYear;
        const month = state.monthNumber;
        const monthDate = new Date(Date.UTC(year, month - 1, 1));
        monthLabel.textContent =
            `${monthFormatter.format(monthDate)} ${year + 543}`;
        previousButton.disabled =
            year < parseIsoDate(state.today).getUTCFullYear() ||
            (year === parseIsoDate(state.today).getUTCFullYear() &&
                month <= parseIsoDate(state.today).getUTCMonth() + 1);
        daysContainer.replaceChildren();

        for (let blank = 0; blank < monthDate.getUTCDay(); blank += 1) {
            const spacer = document.createElement("span");
            spacer.className = "calendar-day-spacer";
            spacer.setAttribute("aria-hidden", "true");
            daysContainer.append(spacer);
        }

        for (const day of data.days) {
            const button = document.createElement("button");
            button.type = "button";
            button.className = "calendar-day";
            button.textContent = parseIsoDate(day.date).getUTCDate().toString();
            button.dataset.date = day.date;
            button.setAttribute("aria-label", day.date);
            if (day.isOccupied) {
                button.classList.add("occupied");
            } else {
                button.classList.add("available");
            }
            if (state.checkIn && day.date === state.checkIn) {
                button.classList.add("selected", "selected-start");
            }
            if (state.checkOut && day.date === state.checkOut) {
                button.classList.add("selected", "selected-end");
            }
            if (state.checkIn && state.checkOut &&
                day.date > state.checkIn && day.date < state.checkOut) {
                button.classList.add("in-range");
            }

            const canStart = day.canCheckIn && state.roomCanBeBooked;
            const canRestartCheckIn = state.checkIn &&
                day.date <= state.checkIn &&
                canStart;
            const canSelectCheckout = state.checkIn &&
                day.date > state.checkIn &&
                state.roomCanBeBooked &&
                !rangeHasConflict(state.checkIn, day.date);
            const canSelect = !state.checkIn
                ? canStart
                : canRestartCheckIn || canStart || canSelectCheckout;
            button.disabled = !canSelect;
            button.addEventListener("click", () => selectDate(day));
            daysContainer.append(button);
        }

        const lastDay = data.days.length;
        const totalCells = monthDate.getUTCDay() + lastDay;
        for (let blank = totalCells; blank % 7 !== 0; blank += 1) {
            const spacer = document.createElement("span");
            spacer.className = "calendar-day-spacer";
            spacer.setAttribute("aria-hidden", "true");
            daysContainer.append(spacer);
        }

        updateSelectionSummary();
    };

    const displayMonth = async (year, month) => {
        state.monthYear = year;
        state.monthNumber = month;
        const version = ++state.renderVersion;
        daysContainer.replaceChildren();
        const loading = document.createElement("p");
        loading.className = "calendar-loading";
        loading.textContent = "กำลังตรวจสอบวันว่าง...";
        daysContainer.append(loading);
        try {
            const data = await loadMonth(year, month);
            if (version === state.renderVersion) {
                renderCalendar(data);
            }
        } catch (error) {
            if (version === state.renderVersion) {
                daysContainer.replaceChildren();
                setError(error.message);
            }
        }
    };

    const selectDate = async day => {
        setError("");
        if (!state.checkIn || day.date <= state.checkIn) {
            if (!day.canCheckIn || !state.roomCanBeBooked) {
                return;
            }
            state.checkIn = day.date;
            state.checkOut = "";
            showPrompt("เลือกวันที่ออกจากที่พัก");
            const month = state.months.get(monthKey(
                state.monthYear,
                state.monthNumber));
            renderCalendar(month);
            return;
        }

        const checkIn = state.checkIn;
        const checkOut = day.date;
        try {
            await ensureMonthsInRange(checkIn, checkOut);
        } catch (error) {
            setError(error.message);
            return;
        }

        if (rangeHasConflict(checkIn, checkOut)) {
            if (day.canCheckIn && state.roomCanBeBooked) {
                state.checkIn = day.date;
                state.checkOut = "";
                showPrompt("ช่วงวันที่เดิมมีการจองทับซ้อน เลือกวันเข้าพักใหม่แล้ว");
                const month = state.months.get(monthKey(
                    state.monthYear,
                    state.monthNumber));
                renderCalendar(month);
                return;
            }

            setError("ช่วงวันที่เลือกมีการจองทับซ้อน กรุณาเลือกวันออกใหม่");
            const month = state.months.get(monthKey(
                state.monthYear,
                state.monthNumber));
            renderCalendar(month);
            return;
        }

        state.checkOut = checkOut;
        showPrompt("ตรวจสอบรายละเอียดการเข้าพักก่อนดำเนินการจอง");
        const month = state.months.get(monthKey(
            state.monthYear,
            state.monthNumber));
        renderCalendar(month);
    };

    const updateSelectionSummary = () => {
        if (!state.checkIn) {
            nightCount.textContent = "-";
            total.textContent = "-";
            continueButton.disabled = true;
            return;
        }

        if (!state.checkOut) {
            prompt.textContent = `Check-in: ${state.checkIn} · กรุณาเลือก Check-out`;
            nightCount.textContent = "-";
            total.textContent = "-";
            continueButton.disabled = true;
            return;
        }

        const nights = Math.round(
            (parseIsoDate(state.checkOut) - parseIsoDate(state.checkIn)) /
            86400000);
        const guests = Number(guestsInput.value);
        const guestsValid = Number.isInteger(guests) &&
            guests > 0 &&
            (!Number.isInteger(state.capacity) || guests <= state.capacity);
        nightCount.textContent = `${nights} คืน`;
        total.textContent = state.rate > 0
            ? moneyFormatter.format(state.rate * nights)
            : "-";
        prompt.textContent =
            `Check-in: ${state.checkIn} · Check-out: ${state.checkOut}`;
        continueButton.disabled =
            nights <= 0 ||
            !guestsValid ||
            !state.roomCanBeBooked ||
            rangeHasConflict(state.checkIn, state.checkOut);
    };

    const openModal = async button => {
        state.roomId = button.dataset.roomId;
        state.roomNumber = button.dataset.roomNumber || "";
        state.roomType = button.dataset.roomType || "";
        state.rate = Number(button.dataset.roomRate) || null;
        state.capacity = Number(button.dataset.roomCapacity) || null;
        state.roomCanBeBooked = false;
        state.checkIn = "";
        state.checkOut = "";
        state.months.clear();
        state.today = "";
        state.lastFocusedElement = button;
        state.renderVersion += 1;
        guestsInput.value = "1";
        guestsInput.removeAttribute("max");
        roomDetails.textContent =
            `ห้อง ${state.roomNumber || "-"} · ${state.roomType || "Room"}`;
        setError("");
        showPrompt("กำลังโหลดปฏิทิน...");
        modal.hidden = false;
        modal.setAttribute("aria-hidden", "false");
        document.body.classList.add("calendar-modal-open");
        modal.querySelector(".calendar-close").focus();

        const initialDate = parseIsoDate(modal.dataset.today);
        await displayMonth(
            initialDate.getUTCFullYear(),
            initialDate.getUTCMonth() + 1);
        if (!state.checkIn) {
            showPrompt("เลือกวันที่เข้าพักเพื่อเริ่มต้น");
        }
    };

    const closeModal = () => {
        state.renderVersion += 1;
        modal.hidden = true;
        modal.setAttribute("aria-hidden", "true");
        document.body.classList.remove("calendar-modal-open");
        state.lastFocusedElement?.focus();
    };

    document.querySelectorAll(".js-open-room-calendar")
        .forEach(button => button.addEventListener("click", () => openModal(button)));
    closeButtons.forEach(button => button.addEventListener("click", closeModal));
    modal.addEventListener("click", event => {
        if (event.target === modal) {
            closeModal();
        }
    });
    document.addEventListener("keydown", event => {
        if (!modal.hidden && event.key === "Escape") {
            closeModal();
        }
    });

    previousButton.addEventListener("click", () => {
        let year = state.monthYear;
        let month = state.monthNumber - 1;
        if (month < 1) {
            month = 12;
            year -= 1;
        }
        displayMonth(year, month);
    });
    nextButton.addEventListener("click", () => {
        let year = state.monthYear;
        let month = state.monthNumber + 1;
        if (month > 12) {
            month = 1;
            year += 1;
        }
        displayMonth(year, month);
    });
    guestsInput.addEventListener("input", updateSelectionSummary);
    continueButton.addEventListener("click", () => {
        if (continueButton.disabled) {
            return;
        }
        const url = new URL(modal.dataset.bookingUrl, window.location.origin);
        url.searchParams.set("roomId", state.roomId);
        url.searchParams.set("checkIn", state.checkIn);
        url.searchParams.set("checkOut", state.checkOut);
        url.searchParams.set("guests", guestsInput.value);
        window.location.assign(url);
    });
})();

(() => {
    const form = document.getElementById("promotion-preview-form");
    if (!form) {
        return;
    }

    const codeInput = document.getElementById("booking-promotion-code");
    const extraBedInput = document.getElementById("booking-extra-bed-count");
    const applyButton = document.getElementById("booking-apply-promotion");
    const feedback = document.getElementById("booking-promotion-feedback");
    const roomSubtotal = document.getElementById("booking-room-subtotal");
    const discount = document.getElementById("booking-discount");
    const extraBedAmount = document.getElementById("booking-extra-bed-amount");
    const totalAmount = document.getElementById("booking-total-amount");
    const confirmButton = document.querySelector("[data-booking-confirm]");
    const confirmForm = document.getElementById("booking-confirm-form");
    const currency = form.dataset.currency || "";
    let requestSequence = 0;
    let activeRequest = null;
    let quoteTimeout = null;

    const formatMoney = value =>
        `${currency}${Number(value).toLocaleString("th-TH", {
            minimumFractionDigits: 2,
            maximumFractionDigits: 2
        })}`;

    const clearAmounts = () => {
        roomSubtotal.textContent = "—";
        discount.textContent = formatMoney(0);
        extraBedAmount.textContent = "—";
        totalAmount.textContent = "—";
    };

    const updateConfirmAvailability = () => {
        if (confirmButton) {
            confirmButton.disabled = false;
        }
    };

    const setFeedback = (message, status) => {
        feedback.textContent = message || "";
        feedback.classList.toggle("promotion-feedback-success", status === "Success");
        feedback.classList.toggle(
            "promotion-feedback-error",
            status !== "Success" && status !== "NoCode");
    };

    const renderQuote = result => {
        if (!result.isValidRequest ||
            result.roomSubtotal == null ||
            result.discountAmount == null ||
            result.extraBedAmount == null ||
            result.totalAmount == null) {
            clearAmounts();
            setFeedback(result.message || "ข้อมูลไม่ถูกต้อง", result.promotionStatus);
            return;
        }

        roomSubtotal.textContent = formatMoney(result.roomSubtotal);
        discount.textContent = formatMoney(result.discountAmount);
        extraBedAmount.textContent = formatMoney(result.extraBedAmount);
        totalAmount.textContent = formatMoney(result.totalAmount);
        setFeedback(result.message, result.promotionStatus);
    };

    const requestQuote = async applyCode => {
        const sequence = ++requestSequence;
        activeRequest?.abort();
        const controller = new AbortController();
        activeRequest = controller;
        applyButton.disabled = true;
        if (applyCode) {
            applyButton.textContent = "กำลังตรวจสอบ...";
        }

        const formData = new FormData(form);
        if (!applyCode) {
            formData.set("PromotionCode", "");
        }

        const antiForgeryToken =
            formData.get("__RequestVerificationToken");

        try {
            const response = await fetch(form.dataset.applyUrl, {
                method: "POST",
                headers: {
                    "RequestVerificationToken": antiForgeryToken
                },
                body: formData,
                signal: controller.signal,
                credentials: "same-origin"
            });
            const result = await response.json();
            if (sequence !== requestSequence) {
                return;
            }

            renderQuote(result);
        } catch (error) {
            if (error.name !== "AbortError" && sequence === requestSequence) {
                clearAmounts();
                setFeedback(
                    "ไม่สามารถตรวจสอบราคาได้ กรุณาลองอีกครั้ง",
                    "InvalidRequest");
            }
        } finally {
            if (sequence === requestSequence) {
                activeRequest = null;
                applyButton.disabled = false;
                applyButton.textContent = "Apply";
            }
        }
    };

    const invalidateAppliedPromotion = () => {
        requestSequence += 1;
        activeRequest?.abort();
        activeRequest = null;
        clearTimeout(quoteTimeout);
        clearAmounts();
        setFeedback(
            "ข้อมูลเปลี่ยนแล้ว กรุณากด Apply ใหม่เพื่อตรวจสอบ Code",
            "NoCode");
        updateConfirmAvailability();
        quoteTimeout = setTimeout(() => requestQuote(false), 250);
    };

    form.addEventListener("submit", event => {
        event.preventDefault();
        clearTimeout(quoteTimeout);
        requestQuote(true);
    });

    codeInput.addEventListener("input", invalidateAppliedPromotion);
    extraBedInput.addEventListener("change", invalidateAppliedPromotion);
    form.querySelectorAll('[name="CheckIn"], [name="CheckOut"], [name="Guests"]')
        .forEach(input => {
            input.addEventListener("input", invalidateAppliedPromotion);
            input.addEventListener("change", invalidateAppliedPromotion);
        });

    confirmForm?.addEventListener("submit", () => {
        const confirmExtraBed = confirmForm.querySelector(
            '[name="ExtraBedCount"]');
        const confirmPromotionCode = confirmForm.querySelector(
            '[name="PromotionCode"]');
        if (confirmExtraBed) {
            confirmExtraBed.value = extraBedInput.value;
        }
        if (confirmPromotionCode) {
            confirmPromotionCode.value = codeInput.value.trim();
        }
    });

    updateConfirmAvailability();
})();
