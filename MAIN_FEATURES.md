# BunonBrain: Main Features

**The AI operations platform for Bangladeshi garment (RMG) factories, in English and বাংলা.**

1. **Live factory monitoring:** a dashboard and monitoring room with camera feeds, a live line board and real-time alerts (SignalR).
2. **Machine monitoring (IoT):** ESP32 sensors over MQTT show each machine as running, idle, stopped or in error, and piece counters give real production counts.
3. **AI quality inspection:** upload a garment photo. The AI marks each defect with a box and explains it (type, cause, what to do). An inspector confirms it, and DHU is calculated automatically.
4. **Computer vision:** YOLO + ByteTrack track people in work zones and detect their posture or movement. No face recognition.
5. **Ask BunonBrain (AI assistant):** answers questions in Bangla or English from the factory's own manuals and SOPs, and shows the sources.
6. **Merchandising:** upload POs and tech packs. The AI pulls out quantity, dates and sizes, and flags any conflict between documents.
7. **Production planning:** order, then line allocation, daily targets and approval. Planned vs actual comes from the machine counters, and delays are flagged during the shift.
8. **Document automation:** 38 factory documents (AQL, GRN, packing list, attendance…) generated from the data and exported to PDF, Excel or Word.
9. **Workers and attendance:** worker register, RFID check-in, shifts, overtime approval and workstation assignments.
10. **Analytics and reports:** production, machine uptime and workforce KPIs, with daily, weekly and monthly reports as CSV or PDF.
11. **Human-in-control AI:** risky AI actions wait in an approval queue, and every decision is audited.
12. **Secure role-based access:** 8 roles, each with its own menu. Users can be limited to one factory or one production line.
