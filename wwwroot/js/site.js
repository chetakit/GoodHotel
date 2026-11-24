// ---------- ให้รู้ว่ากำลังโหลด และกันกดซ้ำ ----------
// ฐานข้อมูลอยู่ไกล แต่ละหน้าใช้เวลาเสี้ยววินาที: แสดงแถบโหลดด้านบนทันทีที่กด
// และเมื่อส่งฟอร์มแล้ว ปุ่มจะหมุนรอ กดซ้ำไม่ได้ (กันจองซ้ำ/เช็คอินซ้ำ)
(() => {
    const bar = document.createElement("div");
    bar.className = "page-loading";
    document.body.appendChild(bar);

    const start = () => bar.classList.add("is-active");
    const reset = () => {
        bar.classList.remove("is-active");
        document.querySelectorAll(".is-busy").forEach(button => button.classList.remove("is-busy"));
        document.querySelectorAll("form[data-submitting]").forEach(form => delete form.dataset.submitting);
    };

    document.addEventListener("click", event => {
        const link = event.target.closest("a[href]");
        if (!link || event.defaultPrevented || event.button !== 0 ||
            event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
        const url = new URL(link.href, location.href);
        const samePage = url.pathname === location.pathname && url.search === location.search;
        if (link.target || link.hasAttribute("download") || url.origin !== location.origin ||
            samePage || url.pathname.includes("/Export") || link.dataset.bsToggle) return;
        start();
    });

    document.addEventListener("submit", event => {
        const form = event.target;
        if (event.defaultPrevented) return;
        if (form.dataset.submitting) {
            event.preventDefault();
            return;
        }
        form.dataset.submitting = "true";
        // ไม่ใช้ disabled เพราะปุ่มบางปุ่มส่งค่า name/value ไปด้วย (เช่น ยืนยัน/ปฏิเสธ)
        event.submitter?.classList.add("is-busy");
        start();
    });

    // กดย้อนกลับแล้วหน้าเดิมโผล่มาจาก cache: เคลียร์สถานะรอ
    window.addEventListener("pageshow", reset);
})();

// ---------- นับถอยหลังเวลาชำระเงิน ----------
// หมดเวลาแล้ว: เปลี่ยนข้อความ และซ่อนปุ่ม/ฟอร์มชำระเงินที่มี data-hide-on-expire
(() => {
    const boxes = document.querySelectorAll("[data-deadline]");
    if (boxes.length === 0) return;

    const tick = () => {
        let anyExpired = false;
        boxes.forEach(box => {
            const left = Math.max(0, Math.floor((new Date(box.dataset.deadline) - Date.now()) / 1000));
            const counter = box.querySelector("[data-countdown]");
            if (counter) counter.textContent = `${String(Math.floor(left / 60)).padStart(2, "0")}:${String(left % 60).padStart(2, "0")}`;
            if (left === 0) {
                box.querySelector("[data-deadline-open]")?.setAttribute("hidden", "");
                box.querySelector("[data-deadline-closed]")?.removeAttribute("hidden");
                anyExpired = true;
            }
        });
        if (anyExpired) {
            document.querySelectorAll("[data-hide-on-expire]").forEach(el => el.setAttribute("hidden", ""));
        }
    };
    tick();
    setInterval(tick, 1000);
})();

// ---------- ปฏิทินเลือกวันเข้าพัก ----------
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
        .flatMap(month => month.blockedPeriods);

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
            blockedPeriods: (result.blockedPeriods || []).map(period => ({
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
            `ห้อง ${result.roomNumber || "-"} · ${result.roomType || "ไม่ระบุประเภท"} · ` +
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
                showPrompt("ช่วงวันที่เดิมไม่พร้อมให้จอง เลือกวันเข้าพักใหม่แล้ว");
                const month = state.months.get(monthKey(
                    state.monthYear,
                    state.monthNumber));
                renderCalendar(month);
                return;
            }

            setError("ช่วงวันที่เลือกไม่พร้อมให้จอง กรุณาเลือกวันออกใหม่");
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
            `ห้อง ${state.roomNumber || "-"} · ${state.roomType || "ไม่ระบุประเภท"}`;
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

// หน้ายืนยันการจอง: เปลี่ยนจำนวนเตียงเสริมแล้วขอราคาใหม่จาก server
// (server เป็นคนเลือกโปรที่ดีที่สุดให้อัตโนมัติ หน้าเว็บแค่แสดงผล)
(() => {
    const form = document.getElementById("promotion-preview-form");
    if (!form) {
        return;
    }

    const extraBedInput = document.getElementById("booking-extra-bed-count");
    const feedback = document.getElementById("booking-promotion-feedback");
    const promotionName = document.getElementById("booking-promotion-name");
    const roomSubtotal = document.getElementById("booking-room-subtotal");
    const discount = document.getElementById("booking-discount");
    const extraBedAmount = document.getElementById("booking-extra-bed-amount");
    const totalAmount = document.getElementById("booking-total-amount");
    const confirmForm = document.getElementById("booking-confirm-form");
    const currency = form.dataset.currency || "";
    let latestRequest = 0;

    const formatMoney = value =>
        `${currency}${Number(value).toLocaleString("th-TH", {
            minimumFractionDigits: 2,
            maximumFractionDigits: 2
        })}`;

    const showMessage = (message, isSuccess) => {
        feedback.textContent = message || "";
        feedback.classList.toggle("promotion-feedback-success", isSuccess);
        feedback.classList.toggle("promotion-feedback-error", !isSuccess);
    };

    const render = result => {
        roomSubtotal.textContent = formatMoney(result.roomSubtotal);
        discount.textContent = `-${formatMoney(result.discountAmount)}`;
        extraBedAmount.textContent = formatMoney(result.extraBedAmount);
        totalAmount.textContent = formatMoney(result.totalAmount);
        promotionName.textContent = result.promotionName || "ไม่เข้าเงื่อนไขโปร";
        // server เป็นคนตัดสินโปรที่ใช้ได้จริง: ถ้าโปรที่เลือกใช้ไม่ได้แล้ว ให้ dropdown ตรงกับที่ server ใช้
        const select = document.getElementById("booking-promotion-select");
        if (select && result.promotionId) {
            select.value = String(result.promotionId);
        }
        showMessage(result.message, true);
    };

    const requestQuote = async () => {
        const requestId = ++latestRequest;
        const formData = new FormData(form);
        try {
            const response = await fetch(form.dataset.applyUrl, {
                method: "POST",
                headers: { "RequestVerificationToken": formData.get("__RequestVerificationToken") },
                body: formData,
                credentials: "same-origin"
            });
            const result = await response.json();
            if (requestId !== latestRequest) {
                return; // มีคำขอใหม่กว่าแล้ว
            }

            if (result.isValidRequest) {
                render(result);
            } else {
                showMessage(result.message, false);
            }
        } catch {
            if (requestId === latestRequest) {
                showMessage("ไม่สามารถคำนวณราคาได้ กรุณาลองอีกครั้ง", false);
            }
        }
    };

    const promotionSelect = document.getElementById("booking-promotion-select");

    form.addEventListener("submit", event => event.preventDefault());
    extraBedInput.addEventListener("change", requestQuote);
    promotionSelect?.addEventListener("change", requestQuote);

    // ส่งจำนวนเตียงเสริมและโปรที่เลือกล่าสุดไปกับฟอร์มยืนยันการจอง
    confirmForm?.addEventListener("submit", () => {
        const confirmExtraBed = confirmForm.querySelector('[name="ExtraBedCount"]');
        if (confirmExtraBed) {
            confirmExtraBed.value = extraBedInput.value;
        }
        const confirmPromotion = confirmForm.querySelector('[name="PromotionId"]');
        if (confirmPromotion && promotionSelect) {
            confirmPromotion.value = promotionSelect.value;
        }
    });
})();
