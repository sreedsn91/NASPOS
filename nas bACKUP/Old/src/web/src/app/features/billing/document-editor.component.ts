import { Component, HostListener, OnDestroy, OnInit, ViewChild, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { Auth } from '../../core/auth';
import { GstinErrorPipe, gstinError, normalizeGstin } from '../../core/gstin';
import { applyTax, calculate, defaultTaxType, emptyDocument, emptyLine, lineFromProduct, r2, r4 } from '../../core/calc';
import { DmyPipe, MoneyPipe, QtyPipe, fromLocalInput, toLocalInput } from '../../core/format';
import { Live } from '../../core/live';
import {
  DocumentListItem, LANDED_COST, POSTS_ACCOUNTS, PURCHASE_SIDE, Paged, PartyLookup, ProductLookup, RoundOffMode, SCREEN_FOR, StockDirection,
  TAX_FREE, TaxType, TradeDocument, TradeLine, VOUCHER_LABELS, VoucherType,
} from '../../core/models';
import { Ui } from '../../core/ui';
import { LookupComponent, ModalComponent } from '../../shared/components';
import { BillingRefs, InvoiceOptions, lastSalesperson, partyLabel, partySub, partyTypeFor, productLabel, productSub, rememberSalesperson } from './billing-shared';

const ORDER_SOURCES: Partial<Record<VoucherType, VoucherType[]>> = {
  Sales: ['SalesOrder', 'Estimate'], LocalSales: ['SalesOrder', 'Estimate'], DeliveryNote: ['SalesOrder'],
  Purchase: ['PurchaseOrder'], LocalPurchase: ['PurchaseOrder'], ReceiptNote: ['PurchaseOrder'],
};

@Component({
  selector: 'app-document-editor',
  standalone: true,
  imports: [FormsModule, RouterLink, LookupComponent, ModalComponent, MoneyPipe, QtyPipe, DmyPipe, GstinErrorPipe],
  template: `
  <div class="page wide">
    <div class="page-header">
      <h1>{{ title() }} @if (doc().documentNo) { <span class="muted">· {{ doc().documentNo }}</span> } @else { <span class="muted">· {{ nextNo() }}</span> }</h1>
      @if (doc().cancelled) { <span class="badge danger">CANCELLED {{ doc().cancelReason }}</span> }
      @if (doc().isProcessed) { <span class="badge ok">Converted to bill</span> }
      @if (lockedBy()) { <span class="badge warn">Also open by {{ lockedBy() }}</span> }
      @if (readOnly()) { <span class="badge warn">Read-only</span> }
      <a class="btn ghost" [routerLink]="['/documents', type()]">List</a>
      @if (isEdit()) {
        <button class="btn" (click)="print()" [disabled]="!auth.can(screen(), 'Print')">Print</button>
        <button class="btn" (click)="email()" [disabled]="!auth.can(screen(), 'Email')">E-mail</button>
        <button class="btn" (click)="sms()" [disabled]="!auth.can(screen(), 'Sms')">SMS</button>
        @if (isOrder() && !doc().isProcessed && !doc().cancelled) {
          <button class="btn" (click)="convert()">Convert to bill</button>
        }
        @if (!doc().cancelled && auth.can(screen(), 'Cancel')) { <button class="btn danger" (click)="cancelDoc()">Cancel bill</button> }
        @if (isOrder() && auth.can(screen(), 'Delete')) { <button class="btn danger" (click)="deleteDoc()">Delete</button> }
        <button class="btn" (click)="newDoc()">New <kbd>F8</kbd></button>
      }
      <button class="btn primary" (click)="save()" [disabled]="saving() || readOnly()">Save <kbd>F2</kbd></button>
    </div>

    <div class="doc-layout">
      <div class="stack">
        <div class="card doc-head">
          <div class="form-grid cols-6">
            <label class="field"><span>Date &amp; time</span>
              <input type="datetime-local" [ngModel]="dateInput()" (ngModelChange)="setDate($event)" [disabled]="readOnly()" /></label>
            @if (partyType()) {
              <div class="field span-2"><span>{{ partyType() === 'Supplier' ? 'Supplier' : 'Customer' }} <kbd>F4</kbd>
                <button class="link-btn small" (click)="quickParty.set(true)" [hidden]="readOnly()">+ new</button></span>
                <app-lookup #partyBox [search]="searchParty" [display]="partyLabel" [sub]="partySub" [value]="doc().partyName"
                            placeholder="Name, mobile or code" (picked)="pickParty($event)" [disabled]="readOnly()" />
              </div>
            }
            @if (postsAccounts()) {
              <label class="field"><span>Payment</span>
                <select [(ngModel)]="doc().paymentMode" (ngModelChange)="onPaymentMode()" [disabled]="readOnly()">
                  <option value="Cash">Cash</option><option value="Credit">Credit</option><option value="Mixed">Mixed / Card</option>
                </select></label>
            }
            @if (!taxFree()) {
              <label class="field"><span>Tax type</span>
                <select [(ngModel)]="doc().taxType" (ngModelChange)="recalc()" [disabled]="readOnly()">
                  <option value="IntraState">Intra-state (CGST+SGST)</option><option value="InterState">Inter-state (IGST)</option><option value="NoTax">No tax</option>
                </select></label>
            }
            @if (salesSide()) {
              <label class="field" [class.req]="auth.flag('RequireSalesperson')"><span>Salesperson</span>
                <select [(ngModel)]="doc().salespersonId" [disabled]="readOnly()">
                  <option [ngValue]="null">—</option>
                  @for (e of refs.employees; track e.id) { <option [ngValue]="e.id">{{ e.name }}</option> }
                </select></label>
            }
            @if (auth.flag('EnablePriceList') && salesSide()) {
              <label class="field"><span>Price list</span>
                <select [(ngModel)]="doc().priceListId" [disabled]="readOnly()">
                  <option [ngValue]="null">Default rates</option>
                  @for (p of refs.priceLists; track p.id) { <option [ngValue]="p.id">{{ p.name }}</option> }
                </select></label>
            }
            @if (purchaseSide()) {
              <label class="field"><span>Supplier inv. no</span><input [(ngModel)]="doc().referenceNo" [disabled]="readOnly()" /></label>
              <label class="field"><span>Supplier inv. date</span><input type="date" [(ngModel)]="doc().referenceDate" [disabled]="readOnly()" /></label>
            }
            @if (type() === 'Production') {
              <label class="field"><span>BOM</span>
                <select [(ngModel)]="bomId" [disabled]="readOnly()"><option [ngValue]="null">—</option>
                  @for (b of boms(); track b.id) { <option [ngValue]="b.id">{{ b.name }}</option> }</select></label>
              <label class="field"><span>Finished qty</span>
                <div class="row"><input type="number" class="num" [(ngModel)]="bomQty" /><button class="btn" (click)="loadBom()" [disabled]="!bomId || readOnly()">Load</button></div></label>
            }
            @if (!partyType()) { <span class="span-2"></span> }
            <label class="field span-2"><span>Remarks</span><input [(ngModel)]="doc().remarks" [disabled]="readOnly()" /></label>
            @if (sources().length && !isEdit()) {
              <div class="field"><span>&nbsp;</span><button class="btn" (click)="openOrders()">Import order…</button></div>
            }
            <div class="field"><span>&nbsp;</span><button class="btn ghost" (click)="showMore.set(!showMore())">{{ showMore() ? 'Less' : 'More…' }}</button></div>
          </div>
          @if (party(); as p) {
            <div class="toolbar small" style="margin-top:8px">
              <span class="badge">Balance ₹ {{ p.balance | money }}</span>
              @if (p.creditLimit) { <span class="badge" [class.danger]="p.balance > p.creditLimit">Limit ₹ {{ p.creditLimit | money }}</span> }
              @if (p.gstin) { <span class="badge info">GSTIN {{ p.gstin }}</span> }
              @if (p.loyaltySchemeId) { <span class="badge ok">Points {{ p.loyaltyPoints | qty }}</span> }
              @if (p.discountPercent) { <span class="badge">Disc {{ p.discountPercent }}%</span> }
            </div>
          }
          @if (showMore()) {
            <div class="form-grid cols-6" style="margin-top:10px">
              <label class="field span-2"><span>Party name (print)</span><input [(ngModel)]="doc().partyName" [disabled]="readOnly()" /></label>
              <label class="field"><span>Phone</span><input [(ngModel)]="doc().partyPhone" [disabled]="readOnly()" /></label>
              <label class="field"><span>GSTIN</span><input [(ngModel)]="doc().partyGstin" (change)="onGstin()" maxlength="20" [disabled]="readOnly()" />
                @if (doc().partyGstin | gstinError; as err) { <small class="danger-text">{{ err }}</small> }</label>
              <label class="field"><span>State code</span><input [(ngModel)]="doc().partyStateCode" (change)="autoTax()" maxlength="2" [disabled]="readOnly()" /></label>
              <label class="field"><span>Bill type</span><select [(ngModel)]="doc().billType" [disabled]="readOnly()"><option [ngValue]="null">Auto</option><option value="B2C">B2C</option><option value="B2B">B2B</option></select></label>
              <label class="field span-3"><span>Address</span><input [(ngModel)]="doc().partyAddress" [disabled]="readOnly()" /></label>
              <label class="field"><span>Order details</span><input [(ngModel)]="doc().orderDetails" [disabled]="readOnly()" /></label>
              <label class="field"><span>Despatch details</span><input [(ngModel)]="doc().despatchDetails" [disabled]="readOnly()" /></label>
              <label class="field"><span>Delivery date</span><input type="date" [(ngModel)]="doc().deliveryDate" [disabled]="readOnly()" /></label>
              @if (landedCost()) { <label class="field"><span>Cost factor</span><input type="number" class="num" step="0.01" [(ngModel)]="doc().costFactor" (ngModelChange)="recalc()" [disabled]="readOnly()" /></label> }
              @if (salesSide()) { <label class="check"><input type="checkbox" [(ngModel)]="showProfit" /> Show profit</label> }
            </div>
          }
        </div>

        <div class="card">
          <div class="lines-scroll">
            <table class="line-grid">
              <thead><tr>
                <th>#</th><th style="min-width:220px">Item</th>
                @if (showBatch()) { <th class="w-l">Batch</th> }
                @if (dirColumn()) { <th class="w-s">In/Out</th> }
                @if (auth.flag('EnableUnitConversion')) { <th class="w-s">Unit</th> }
                <th class="w-s num">Qty</th>
                @if (opts().freeQty && !taxFree()) { <th class="w-xs num">Free</th> }
                @if (!stockJournal()) { <th class="w-m num">MRP</th> }
                <th class="w-m num">{{ stockJournal() ? 'Cost' : 'Rate' }}</th>
                @if (!taxFree()) { <th class="w-xs">Incl</th><th class="w-xs num">Disc%</th><th class="w-s num">Disc</th> }
                @if (purchaseSide() && !isOrderType()) { <th class="w-m num">Sale rate</th> }
                @if (expiryColumn()) { <th class="w-l">Expiry</th> }
                @if (opts().extraColumn) { <th class="w-l">{{ opts().extraColumnName }}</th> }
                @if (!taxFree()) { <th class="num">Taxable</th><th class="w-xs num">GST%</th><th class="num">Tax</th> }
                <th class="num">Total</th>
                @if (landedCost() || showProfit) { <th class="num">Cost</th><th class="num">{{ landedCost() ? 'Margin%' : 'Profit' }}</th> }
                <th></th>
              </tr></thead>
              <tbody>
                @for (l of doc().lines; track $index; let i = $index) {
                  <tr [class.focused]="focusRow() === i" [class.bad]="rowBad(l)" [class.warn]="rowWarn(l)" (focusin)="focusRow.set(i)">
                    <td class="ro">{{ i + 1 }}</td>
                    <td class="ro" [title]="l.productCode || ''">{{ l.productName }}
                      @if (l.stock !== undefined && !purchaseSide()) { <span class="muted small"> · stk {{ l.stock | qty }}</span> }</td>
                    @if (showBatch()) {
                      <td>@if (purchaseSide() || (dirColumn() && l.direction === 'In')) {
                        <input [(ngModel)]="l.batchCode" (ngModelChange)="l.batchId = null" placeholder="auto" [disabled]="readOnly()" />
                      } @else { <span class="muted small">{{ l.batchCode }}</span> }</td>
                    }
                    @if (dirColumn()) {
                      <td><select [(ngModel)]="l.direction" (ngModelChange)="recalc()" [disabled]="readOnly()"><option value="In">In</option><option value="Out">Out</option></select></td>
                    }
                    @if (auth.flag('EnableUnitConversion')) {
                      <td>@if (l.altUnit) {
                        <select [ngModel]="l.isAltUnit" (ngModelChange)="setUnit(l, $event)" [disabled]="readOnly()">
                          <option [ngValue]="false">{{ l.mainUnit }}</option><option [ngValue]="true">{{ l.altUnit }}</option></select>
                      } @else { <span class="muted small">{{ l.unit }}</span> }</td>
                    }
                    <td><input type="number" class="num" [(ngModel)]="l.qty" (ngModelChange)="recalc()" (keydown.enter)="focusItem()" [disabled]="readOnly()" [attr.data-row]="i" data-col="qty" /></td>
                    @if (opts().freeQty && !taxFree()) { <td><input type="number" class="num" [(ngModel)]="l.freeQty" (ngModelChange)="recalc()" [disabled]="readOnly()" /></td> }
                    @if (!stockJournal()) { <td><input type="number" class="num" [(ngModel)]="l.mrp" (ngModelChange)="recalc()" [disabled]="readOnly()" /></td> }
                    <td><input type="number" class="num" [(ngModel)]="l.rate" (ngModelChange)="onRate(l)" [disabled]="readOnly() || (opts().disableRateEdit && salesSide())" /></td>
                    @if (!taxFree()) {
                      <td class="center"><input type="checkbox" [(ngModel)]="l.taxInclusive" (ngModelChange)="recalc()" [disabled]="readOnly()" /></td>
                      <td><input type="number" class="num" [(ngModel)]="l.discountPercent" (ngModelChange)="recalc()" [disabled]="readOnly()" /></td>
                      <td><input type="number" class="num" [ngModel]="l.discountAmount" (ngModelChange)="l.discountAmount = +$event; l.discountPercent = 0; recalc()" [disabled]="readOnly()" /></td>
                    }
                    @if (purchaseSide() && !isOrderType()) { <td><input type="number" class="num" [(ngModel)]="l.saleRate" [disabled]="readOnly()" /></td> }
                    @if (expiryColumn()) { <td>@if (l.trackExpiry) { <input type="date" [(ngModel)]="l.expiryDate" [disabled]="readOnly()" /> }</td> }
                    @if (opts().extraColumn) { <td><input [(ngModel)]="l.extraColumn" [disabled]="readOnly()" /></td> }
                    @if (!taxFree()) {
                      <td class="ro num">{{ l.taxable | money }}</td>
                      <td class="ro num">{{ taxPct(l) | qty }}</td>
                      <td class="ro num">{{ lineTax(l) | money }}</td>
                    }
                    <td class="ro num"><b>{{ (taxFree() ? l.qty * l.rate : l.lineTotal) | money }}</b></td>
                    @if (landedCost() || showProfit) {
                      <td class="ro num">{{ l.cost | money }}</td>
                      <td class="ro num">{{ (landedCost() ? l.profitPercent : l.profit) | money }}</td>
                    }
                    <td class="actions">
                      @if (l.itemClass !== 'Service' && ($any(l).sqftPerBox > 0)) { <button class="btn ghost sm" (click)="sqft(l)" title="Sq.ft → boxes">ft²</button> }
                      <button class="btn ghost sm" (click)="removeLine(i)" [disabled]="readOnly()" aria-label="Remove line">✕</button></td>
                  </tr>
                }
                @if (!readOnly()) {
                  <tr>
                    <td class="ro">{{ doc().lines.length + 1 }}</td>
                    <td [attr.colspan]="20">
                      <app-lookup #itemBox [search]="searchProduct" [display]="productLabel" [sub]="productSub" [clearOnPick]="true"
                                  placeholder="Scan barcode or type item name / code (F3)" (picked)="addProduct($event)" (entered)="scan($event)" />
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </div>
        <div class="shortcut-bar">
          <span><kbd>F2</kbd> Save</span><span><kbd>F3</kbd> Item</span><span><kbd>F4</kbd> Party</span>
          @if (postsAccounts() && salesSide()) { <span><kbd>F6</kbd> Mixed payment</span> }
          <span><kbd>F8</kbd> New</span><span><kbd>Esc</kbd> close dialogs</span>
          <span class="grow"></span><span>{{ doc().lines.length }} lines · qty {{ totalQty() | qty }}</span>
        </div>
      </div>

      <div class="card">
        <section class="totals">
          <div class="t-row sub"><span>Gross</span><span>{{ doc().grossAmount | money }}</span></div>
          @if (!taxFree()) {
            <div class="t-row sub"><span>Item discount</span><span>{{ doc().itemDiscount | money }}</span></div>
            <div class="t-row"><span>Bill disc %</span><input type="number" class="num" [(ngModel)]="doc().billDiscountPercent" (ngModelChange)="recalc()" [disabled]="readOnly()" /></div>
            <div class="t-row"><span>Bill disc ₹</span><input type="number" class="num" [ngModel]="doc().billDiscount" (ngModelChange)="doc().billDiscount = +$event; doc().billDiscountPercent = 0; recalc()" [disabled]="readOnly() || doc().billDiscountPercent > 0" /></div>
            <hr />
            <div class="t-row sub"><span>Taxable</span><span>{{ doc().taxableAmount | money }}</span></div>
            @if (doc().exemptAmount) { <div class="t-row sub"><span>Exempt</span><span>{{ doc().exemptAmount | money }}</span></div> }
            @if (doc().cgstAmount) { <div class="t-row sub"><span>CGST</span><span>{{ doc().cgstAmount | money }}</span></div> }
            @if (doc().sgstAmount) { <div class="t-row sub"><span>SGST</span><span>{{ doc().sgstAmount | money }}</span></div> }
            @if (doc().igstAmount) { <div class="t-row sub"><span>IGST</span><span>{{ doc().igstAmount | money }}</span></div> }
            @if (doc().cessAmount + doc().addCessAmount) { <div class="t-row sub"><span>Cess</span><span>{{ doc().cessAmount + doc().addCessAmount | money }}</span></div> }
            @if (doc().kfCessAmount) { <div class="t-row sub"><span>KF cess</span><span>{{ doc().kfCessAmount | money }}</span></div> }
            @if (doc().serviceTaxAmount) { <div class="t-row sub"><span>Service tax</span><span>{{ doc().serviceTaxAmount + doc().serviceCessAmount | money }}</span></div> }
            @if (postsAccounts() || isOrderType()) {
              <div class="t-row"><span>Other exp.</span>
                <span class="row">@if (purchaseSide()) {<input type="number" class="num" style="width:52px" title="GST %" [(ngModel)]="doc().otherExpenseTaxRate" (ngModelChange)="recalc()" [disabled]="readOnly()" />}
                <input type="number" class="num" [(ngModel)]="doc().otherExpense" (ngModelChange)="recalc()" [disabled]="readOnly()" /></span></div>
              <div class="t-row"><span>Freight</span>
                <span class="row">@if (purchaseSide()) {<input type="number" class="num" style="width:52px" title="GST %" [(ngModel)]="doc().freightTaxRate" (ngModelChange)="recalc()" [disabled]="readOnly()" />}
                <input type="number" class="num" [(ngModel)]="doc().freight" (ngModelChange)="recalc()" [disabled]="readOnly()" /></span></div>
            }
            <div class="t-row"><span>Round off</span>
              <span class="row"><select [(ngModel)]="doc().roundOffMode" (ngModelChange)="recalc()" style="width:72px" [disabled]="readOnly()">
                <option value="Auto">Auto</option><option value="Up">Up</option><option value="Down">Down</option><option value="None">None</option></select>
              <span style="width:60px;text-align:right">{{ doc().roundOff | money }}</span></span></div>
            @if (postsAccounts()) {
              <div class="t-row"><span>Cash disc %</span><input type="number" class="num" [(ngModel)]="doc().cashDiscountPercent" (ngModelChange)="recalc()" [disabled]="readOnly()" /></div>
              <div class="t-row"><span>Cash disc ₹</span><input type="number" class="num" [ngModel]="doc().cashDiscount" (ngModelChange)="doc().cashDiscount = +$event; doc().cashDiscountPercent = 0; recalc()" [disabled]="readOnly() || doc().cashDiscountPercent > 0" /></div>
            }
            @if (canRedeem()) {
              <div class="t-row"><span>Redeem points</span><input type="number" class="num" [(ngModel)]="doc().pointsRedeemed" (ngModelChange)="onRedeem()" [disabled]="readOnly()" /></div>
            }
          }
          @if (type() === 'Production') {
            <div class="t-row"><span>Labour cost</span><input type="number" class="num" [(ngModel)]="doc().labourCost" [disabled]="readOnly()" /></div>
            <div class="t-row"><span>Other cost</span><input type="number" class="num" [(ngModel)]="doc().otherCost" [disabled]="readOnly()" /></div>
          }
          <div class="grand">₹ {{ (taxFree() ? stockValue() : doc().billAmount) | money }}</div>
          @if (doc().totalMrp > doc().billAmount && salesSide()) { <div class="t-row ok-text"><span>You saved</span><span>₹ {{ doc().totalMrp - doc().billAmount | money }}</span></div> }
          @if (showProfit && salesSide()) { <div class="t-row"><span>Bill profit</span><span>{{ doc().profit | money }}</span></div> }
          @if (doc().paymentMode === 'Mixed' && postsAccounts()) {
            <hr />
            <div class="t-row sub"><span>Card / bank</span><span>{{ doc().cardAmount | money }}</span></div>
            <div class="t-row sub"><span>Cash</span><span>{{ doc().cashAmount | money }}</span></div>
            <div class="t-row sub"><span>Credit</span><span>{{ doc().creditAmount | money }}</span></div>
            <button class="btn sm" (click)="openMixed()" [disabled]="readOnly()">Edit split <kbd>F6</kbd></button>
          }
          @if (doc().pointsEarned) { <div class="t-row ok-text"><span>Points earned</span><span>{{ doc().pointsEarned | qty }}</span></div> }
          @if (isEdit()) { <div class="muted small" style="margin-top:8px">Saved by {{ doc().userName }} at {{ doc().counter }} · version {{ doc().version }}</div> }
        </section>
      </div>
    </div>
  </div>

  @if (mixed()) {
    <app-modal title="Mixed payment" size="sm" (closed)="mixed.set(false)">
      <div class="stack">
        <div class="t-row"><b>Bill amount</b> <b>₹ {{ doc().billAmount | money }}</b></div>
        <label class="field"><span>Card / bank amount</span><input type="number" class="num" [(ngModel)]="doc().cardAmount" (ngModelChange)="balanceMixed('card')" autofocus /></label>
        <label class="field"><span>Card / bank ledger</span>
          <select [(ngModel)]="doc().cardLedgerId"><option [ngValue]="null">Bank (default)</option>@for (b of refs.bankLedgers; track b.id) {<option [ngValue]="b.id">{{ b.name }}</option>}</select></label>
        <div class="form-grid cols-2">
          <label class="field"><span>Card last 4 digits</span><input [(ngModel)]="doc().cardLast4" maxlength="4" inputmode="numeric" pattern="[0-9]{4}" /></label>
          <label class="field"><span>Reference / UPI txn</span><input [(ngModel)]="doc().paymentReference" /></label>
        </div>
        <label class="field"><span>Cash amount</span><input type="number" class="num" [(ngModel)]="doc().cashAmount" (ngModelChange)="balanceMixed('cash')" /></label>
        <label class="field"><span>Credit (on account)</span><input type="number" class="num" [(ngModel)]="doc().creditAmount" /></label>
        <div class="muted small">Only the last four card digits are recorded. Full card numbers and CVV are never stored.</div>
        @if (mixedDiff() !== 0) { <div class="warn-box">Split differs from the bill by ₹ {{ mixedDiff() | money }}</div> }
      </div>
      <button footer class="btn primary" (click)="mixed.set(false)">Done</button>
    </app-modal>
  }

  @if (cashDesk()) {
    <app-modal title="Cash desk" size="sm" (closed)="cashDesk.set(false)">
      <div class="stack">
        <div class="t-row"><b>Bill amount</b><b>₹ {{ doc().billAmount | money }}</b></div>
        <label class="field"><span>Tendered</span><input type="number" class="num" [(ngModel)]="tendered" autofocus (keydown.enter)="confirmCashDesk()" /></label>
        <div class="denoms">@for (d of [10, 20, 50, 100, 200, 500, 2000]; track d) { <button (click)="tendered = (tendered || 0) + d">+{{ d }}</button> }
          <button (click)="tendered = doc().billAmount">Exact</button></div>
        <div class="grand">Change ₹ {{ (tendered || 0) - doc().billAmount | money }}</div>
      </div>
      <button footer class="btn primary" (click)="confirmCashDesk()">Save bill</button>
    </app-modal>
  }

  @if (ordersModal()) {
    <app-modal title="Import order / estimate" size="lg" (closed)="ordersModal.set(false)">
      <table class="grid">
        <thead><tr><th>Type</th><th>No</th><th>Date</th><th>Party</th><th class="num">Amount</th></tr></thead>
        <tbody>
          @for (o of orders(); track o.id) {
            <tr class="clickable" (click)="importOrder(o)"><td>{{ labels[o.voucherType] }}</td><td>{{ o.documentNo }}</td><td>{{ o.date | dmy }}</td>
              <td>{{ o.partyName }}</td><td class="num">{{ o.billAmount | money }}</td></tr>
          } @empty { <tr><td colspan="5" class="empty">No open orders.</td></tr> }
        </tbody>
      </table>
    </app-modal>
  }

  @if (quickParty()) {
    <app-modal [title]="'New ' + (partyType() === 'Supplier' ? 'supplier' : 'customer')" size="sm" (closed)="quickParty.set(false)">
      <div class="form-grid cols-2">
        <label class="field req span-2"><span>Name</span><input [(ngModel)]="np.name" autofocus /></label>
        <label class="field"><span>Mobile</span><input [(ngModel)]="np.mobile" /></label>
        <label class="field"><span>State code</span><input [(ngModel)]="np.stateCode" maxlength="2" /></label>
        <label class="field span-2"><span>GSTIN</span><input [(ngModel)]="np.gstin" maxlength="20" style="text-transform:uppercase" />
          @if (np.gstin | gstinError: (np.stateCode || null); as err) { <small class="danger-text">{{ err }}</small> }</label>
        <label class="field span-2"><span>Address</span><input [(ngModel)]="np.address" /></label>
      </div>
      <button footer class="btn primary" (click)="createParty()">Create</button>
    </app-modal>
  }
  `,
})
export class DocumentEditorComponent implements OnInit, OnDestroy {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private live = inject(Live);
  refs = new BillingRefs(this.api);
  labels = VOUCHER_LABELS;
  partyLabel = partyLabel;
  partySub = partySub;
  productLabel = productLabel;
  productSub = productSub;
  @ViewChild('itemBox') itemBox?: LookupComponent<ProductLookup>;
  @ViewChild('partyBox') partyBox?: LookupComponent<PartyLookup>;

  type = signal<VoucherType>('Sales');
  doc = signal<TradeDocument>(emptyDocument('Sales'));
  opts = signal<InvoiceOptions>({ freeQty: true, extraColumnName: 'Remarks' } as InvoiceOptions);
  party = signal<PartyLookup | null>(null);
  nextNo = signal('');
  saving = signal(false);
  focusRow = signal(-1);
  showMore = signal(false);
  mixed = signal(false);
  cashDesk = signal(false);
  ordersModal = signal(false);
  quickParty = signal(false);
  orders = signal<DocumentListItem[]>([]);
  boms = signal<{ id: number; name: string }[]>([]);
  lockedBy = signal<string | null>(null);
  tick = signal(0);
  showProfit = false;
  tendered: number | null = null;
  bomId: number | null = null;
  bomQty = 1;
  np = { name: '', mobile: '', stateCode: '', gstin: '', address: '' };
  private lockKey = '';
  private resolveCashDesk?: (ok: boolean) => void;

  screen = computed(() => SCREEN_FOR[this.type()] ?? 'Sales');
  title = computed(() => VOUCHER_LABELS[this.type()]);
  isEdit = computed(() => this.doc().id > 0);
  readOnly = computed(() => !!this.doc().cancelled || !!this.doc().isProcessed || !!this.auth.session()?.financialYear.isClosed
    || (this.isEdit() ? !this.auth.can(this.screen(), 'Edit') : !this.auth.can(this.screen(), 'Add')));
  partyType = computed(() => partyTypeFor(this.type()));
  purchaseSide = computed(() => PURCHASE_SIDE.includes(this.type()));
  salesSide = computed(() => ['Sales', 'LocalSales', 'POSSales', 'Estimate', 'SalesOrder', 'DeliveryNote'].includes(this.type()));
  postsAccounts = computed(() => POSTS_ACCOUNTS.includes(this.type()));
  landedCost = computed(() => LANDED_COST.includes(this.type()));
  taxFree = computed(() => TAX_FREE.includes(this.type()));
  stockJournal = computed(() => ['StockAdjustment', 'Production', 'OpeningStock'].includes(this.type()));
  dirColumn = computed(() => ['StockAdjustment', 'Production'].includes(this.type()));
  isOrderType = computed(() => ['Estimate', 'SalesOrder', 'PurchaseOrder'].includes(this.type()));
  isOrder = computed(() => this.isOrderType() && this.isEdit());
  showBatch = computed(() => this.auth.flag('EnableBatch'));
  expiryColumn = computed(() => this.auth.flag('EnableExpiry') && (this.purchaseSide() || this.type() === 'Production'));
  sources = computed(() => ORDER_SOURCES[this.type()] ?? []);
  canRedeem = computed(() => { this.tick(); const p = this.party(); return this.salesSide() && this.postsAccounts() && !!p?.loyaltySchemeId && p.loyaltyPoints >= p.loyaltyTarget && p.loyaltyPoints > 0; });
  dateInput = computed(() => { this.tick(); return toLocalInput(this.doc().date); });
  totalQty = computed(() => { this.tick(); return this.doc().lines.reduce((s, l) => s + (+l.qty || 0), 0); });
  stockValue = computed(() => { this.tick(); return r2(this.doc().lines.reduce((s, l) => s + (+l.qty || 0) * (+l.rate || 0) * (this.type() === 'StockAdjustment' && l.direction === 'Out' ? -1 : 1), 0)); });
  mixedDiff = computed(() => { this.tick(); const d = this.doc(); return r2(d.billAmount - (+d.cardAmount || 0) - (+d.cashAmount || 0) - (+d.creditAmount || 0)); });

  searchParty = (term: string) => this.api.get<PartyLookup[]>('/ledgers/parties', { type: this.partyType() ?? 'Customer', search: term, take: 15 });
  searchProduct = (term: string) => this.api.get<ProductLookup[]>('/products/lookup', { term, priceListId: this.doc().priceListId ?? undefined, purchase: this.purchaseSide(), take: 25 });

  ngOnInit(): void {
    this.route.paramMap.subscribe(async pm => {
      const type = pm.get('type') as VoucherType;
      const id = pm.get('id');
      this.type.set(type);
      await this.releaseLock();
      try {
        await this.ui.run(async () => {
          await this.refs.load();
          this.opts.set(await this.api.get<InvoiceOptions>(`/company/invoice-options/${type}`));
          if (type === 'Production') this.boms.set(await this.api.get('/boms'));
          if (id && id !== 'new') await this.loadDoc(+id);
          else await this.newDoc(false);
          const from = this.route.snapshot.queryParamMap.get('from');
          if (from && (!id || id === 'new')) await this.importOrderById(+from);
        });
      } catch (e) { this.ui.error(errorMessage(e)); }
    });
  }

  ngOnDestroy(): void { this.releaseLock(); }

  private async releaseLock(): Promise<void> {
    if (this.lockKey) { await this.live.close(this.lockKey); this.lockKey = ''; this.lockedBy.set(null); }
  }

  async newDoc(navigate = true): Promise<void> {
    if (navigate && this.isEdit()) { this.router.navigate(['/doc', this.type(), 'new']); return; }
    const d = emptyDocument(this.type(), (this.auth.setting('DefaultRoundOff') || 'Auto') as RoundOffMode);
    d.taxType = this.taxFree() ? 'NoTax' : 'IntraState';
    if (this.salesSide()) d.salespersonId = lastSalesperson();
    this.party.set(null);
    this.doc.set(d);
    this.tick.update(t => t + 1);
    this.nextNo.set((await this.api.get<{ number: string }>('/documents/next-number', { type: this.type() })).number);
    setTimeout(() => this.focusItem(), 50);
  }

  private async loadDoc(id: number): Promise<void> {
    const d = await this.api.get<TradeDocument>(`/documents/${id}`);
    if (d.voucherType !== this.type()) { this.router.navigate(['/doc', d.voucherType, id], { replaceUrl: true }); return; }
    d.lines.forEach(l => { l.pCgst = l.cgstRate; l.pSgst = l.sgstRate; l.pIgst = l.igstRate; l.pCess = l.cessRate; l.pKf = l.kfCessRate;
      l.pAddCess = l.unitFactor ? l.addCessRate / l.unitFactor : l.addCessRate; });
    this.doc.set(d);
    this.party.set(null);
    if (d.partyLedgerId) {
      const found = await this.api.get<PartyLookup[]>('/ledgers/parties', { type: this.partyType() ?? 'Customer', search: d.partyName ?? '', take: 20 });
      this.party.set(found.find(p => p.id === d.partyLedgerId) ?? null);
    }
    this.tick.update(t => t + 1);
    this.lockKey = `doc:${id}`;
    this.lockedBy.set(await this.live.open(this.lockKey));
  }

  setDate(v: string): void { if (v) { this.doc().date = fromLocalInput(v); this.tick.update(t => t + 1); } }

  pickParty(p: PartyLookup): void {
    const d = this.doc();
    this.party.set(p);
    d.partyLedgerId = p.id;
    d.partyName = p.name;
    d.partyPhone = p.mobile;
    d.partyAddress = p.address;
    d.partyGstin = p.gstin;
    d.partyStateCode = p.stateCode;
    if (p.priceListId) d.priceListId = p.priceListId;
    if (p.salespersonId && this.salesSide()) d.salespersonId = p.salespersonId;
    if (!p.isWalkIn && this.postsAccounts() && this.purchaseSide()) d.paymentMode = 'Credit';
    if (p.discountPercent && this.salesSide()) d.lines.forEach(l => { if (!l.discountPercent) l.discountPercent = p.discountPercent; });
    this.autoTax();
    setTimeout(() => this.focusItem());
  }

  onGstin(): void {
    const d = this.doc();
    d.partyGstin = normalizeGstin(d.partyGstin);
    if (d.partyGstin.length === 15 && !gstinError(d.partyGstin)) d.partyStateCode = d.partyGstin.substring(0, 2);
    this.autoTax();
  }

  autoTax(): void {
    const d = this.doc();
    if (!this.taxFree()) d.taxType = defaultTaxType(this.auth.session()?.company.stateCode, d.partyStateCode, d.partyGstin);
    this.recalc();
  }

  onPaymentMode(): void {
    if (this.doc().paymentMode === 'Mixed') this.openMixed();
    this.recalc();
  }

  focusItem(): void { this.itemBox?.focus(); }

  async scan(term: string): Promise<void> {
    try {
      const rows = await this.api.get<ProductLookup[]>('/products/lookup', { term, priceListId: this.doc().priceListId ?? undefined, purchase: this.purchaseSide(), exact: true });
      if (rows.length === 0) { this.ui.error(`No item for "${term}".`); return; }
      if (rows.length === 1) this.addProduct(rows[0]);
      else this.addProduct(rows.find(r => r.stock > 0) ?? rows[0]);
      if (this.itemBox) this.itemBox.text = '';
    } catch (e) { this.ui.error(errorMessage(e)); }
  }

  addProduct(p: ProductLookup): void {
    const d = this.doc();
    const purchase = this.purchaseSide();
    // Scanner behaviour: same item + batch increments the existing row.
    const existing = d.lines.find(l => l.productId === p.productId && (l.batchId ?? null) === (p.batchId ?? null) && !l.isAltUnit && !p.scaleQty);
    if (existing && (this.opts().scannerMode || !purchase)) {
      existing.qty = (+existing.qty || 0) + 1;
    } else {
      const line = lineFromProduct(p, this.type(), {
        inclusive: this.opts().inclusiveUnitPrice && !purchase ? true : undefined,
        kfCess: this.auth.flag('EnableKfCess') && d.billType !== 'B2B',
        purchase,
      });
      (line as any).sqftPerBox = p.sqftPerBox;
      (line as any).piecesPerBox = p.piecesPerBox;
      if (this.dirColumn()) {
        line.direction = this.type() === 'Production' ? 'Out' : 'In';
        line.rate = p.cost || p.purchaseRate;
      }
      if (this.type() === 'OpeningStock') line.rate = p.purchaseRate;
      if (this.party()?.discountPercent && this.salesSide() && !line.discountPercent) line.discountPercent = this.party()!.discountPercent;
      d.lines.push(line);
      this.focusRow.set(d.lines.length - 1);
      if (!this.opts().scannerMode && !p.scaleQty) {
        setTimeout(() => (document.querySelector(`input[data-row="${d.lines.length - 1}"][data-col="qty"]`) as HTMLInputElement | null)?.select());
      }
    }
    this.recalc();
    if (this.opts().scannerMode || p.scaleQty) this.focusItem();
  }

  setUnit(l: TradeLine, alt: boolean): void {
    const oldFactor = l.unitFactor || 1;
    l.isAltUnit = alt;
    l.unitFactor = alt ? l.conversionFactor || 1 : 1;
    l.unit = alt ? l.altUnit ?? l.unit : l.mainUnit ?? l.unit;
    const k = l.unitFactor / oldFactor;
    l.rate = r4(l.rate * k);
    l.mrp = r2(l.mrp * k);
    l.saleRate = r4(l.saleRate * k);
    applyTax(l, this.type());
    this.recalc();
  }

  onRate(l: TradeLine): void { applyTax(l, this.type()); this.recalc(); }

  async sqft(l: TradeLine): Promise<void> {
    const per = (l as any).sqftPerBox as number;
    const v = await this.ui.prompt('Square feet to boxes', `Area in sq.ft (${per} sq.ft per box)`, { type: 'number' });
    if (!v) return;
    l.qty = Math.ceil(+v / per);
    this.recalc();
  }

  async removeLine(i: number): Promise<void> {
    const d = this.doc();
    const l = d.lines[i];
    d.lines.splice(i, 1);
    this.recalc();
    if (this.salesSide() || this.type() === 'POSSales') {
      try {
        await this.api.post('/documents/void-items', { screen: this.screen(), documentNo: d.documentNo ?? this.nextNo(), productId: l.productId,
          productName: l.productName, batch: l.batchCode, qty: l.qty, rate: l.rate, mrp: l.mrp });
      } catch { /* audit best effort must not block billing; server logs failures */ }
    }
  }

  taxPct(l: TradeLine): number {
    const t = this.doc().taxType;
    return t === 'InterState' ? l.igstRate : t === 'IntraState' ? l.cgstRate + l.sgstRate : 0;
  }
  lineTax(l: TradeLine): number { return r2(l.cgstAmount + l.sgstAmount + l.igstAmount + l.cessAmount + l.addCessAmount + l.kfCessAmount); }
  rowBad(l: TradeLine): boolean { return this.landedCost() && l.mrp > 0 && l.cost >= l.mrp / (l.unitFactor || 1); }
  rowWarn(l: TradeLine): boolean {
    return !this.purchaseSide() && l.itemClass === 'Goods' && l.stock !== undefined && ((l.qty || 0) + (l.freeQty || 0)) * (l.unitFactor || 1) > (l.stock ?? 0)
      && this.type() !== 'SalesReturn' && !this.isOrderType() && l.direction !== 'In';
  }

  onRedeem(): void {
    const p = this.party();
    const d = this.doc();
    if (p && d.pointsRedeemed > p.loyaltyPoints) d.pointsRedeemed = p.loyaltyPoints;
    d.redeemAmount = r2(d.pointsRedeemed || 0);
    this.recalc();
  }

  recalc(): void {
    const d = this.doc();
    if (!this.taxFree()) {
      d.lines.forEach(l => applyTax(l, this.type()));
      calculate(d, {
        serviceTaxRate: +this.auth.setting('ServiceTaxRate') || 0, serviceCessRate: +this.auth.setting('ServiceCessRate') || 0,
        taxClaimable: this.auth.flag('InputTaxClaimable') && d.taxType !== 'NoTax',
      });
    } else {
      d.lines.forEach(l => { l.gross = r2(l.qty * l.rate); l.taxable = l.gross; l.lineTotal = l.gross; });
      d.billAmount = this.stockValue();
    }
    if (d.paymentMode === 'Mixed' && this.postsAccounts() && !this.mixed()) {
      const rest = r2(d.billAmount - (+d.cardAmount || 0) - (+d.creditAmount || 0));
      d.cashAmount = Math.max(0, rest);
    }
    this.tick.update(t => t + 1);
  }

  openMixed(): void {
    const d = this.doc();
    if (!d.cardAmount && !d.cashAmount && !d.creditAmount) d.cardAmount = d.billAmount;
    this.balanceMixed('card');
    this.mixed.set(true);
  }

  balanceMixed(changed: 'card' | 'cash'): void {
    const d = this.doc();
    if (changed === 'card') d.cashAmount = Math.max(0, r2(d.billAmount - (+d.cardAmount || 0) - (+d.creditAmount || 0)));
    else d.creditAmount = this.party() && !this.party()!.isWalkIn ? Math.max(0, r2(d.billAmount - (+d.cardAmount || 0) - (+d.cashAmount || 0))) : 0;
    this.tick.update(t => t + 1);
  }

  confirmCashDesk(): void { this.cashDesk.set(false); this.resolveCashDesk?.(true); }

  private cashDeskPrompt(): Promise<boolean> {
    this.tendered = null;
    this.cashDesk.set(true);
    return new Promise(res => (this.resolveCashDesk = res));
  }

  async save(): Promise<void> {
    if (this.readOnly() || this.saving()) return;
    const d = this.doc();
    if (!d.lines.length) { this.ui.error('Add at least one item.'); return; }
    if (this.salesSide() && this.auth.flag('RequireSalesperson') && !d.salespersonId && this.postsAccounts()) { this.ui.error('Select a salesperson.'); return; }
    if (d.paymentMode === 'Mixed' && this.postsAccounts() && this.salesSide() && this.mixedDiff() !== 0) { this.openMixed(); this.ui.error('Payment split must equal the bill amount.'); return; }
    if (!this.isEdit() && this.opts().cashDeskOnSave && d.paymentMode === 'Cash' && this.salesSide() && this.postsAccounts()) {
      if (!(await this.cashDeskPrompt())) return;
      d.tenderedAmount = this.tendered || 0;
    }
    this.saving.set(true);
    try {
      const body = { ...d, lines: d.lines.map(l => ({ ...l, batchId: l.batchId || null })) };
      const saved = this.isEdit()
        ? await this.api.put<TradeDocument>(`/documents/${d.id}`, body)
        : await this.api.post<TradeDocument>('/documents', body);
      rememberSalesperson(saved.salespersonId);
      this.ui.ok(`${VOUCHER_LABELS[saved.voucherType]} ${saved.documentNo} saved · ₹ ${saved.billAmount.toFixed(2)}`);
      for (const w of saved.warnings ?? []) this.ui.toast(w, 'warn');
      await this.afterSave(saved);
    } catch (e) {
      this.ui.error(errorMessage(e));
    } finally {
      this.saving.set(false);
    }
  }

  private async afterSave(saved: TradeDocument): Promise<void> {
    const o = this.opts();
    if (o.printOnSave && this.auth.can(this.screen(), 'Print')) {
      const go = !o.confirmPrint || await this.ui.confirm('Print', `Print ${saved.documentNo}?`, { okText: 'Print' });
      if (go) {
        const url = `/documents/${saved.id}/pdf${o.printSchemeId ? '?scheme=' + o.printSchemeId : ''}`;
        if (o.showPreview) await this.api.openPdf(url); else await this.api.printPdf(url);
      }
    }
    if (this.isEdit()) { await this.loadDoc(saved.id); return; }
    await this.newDoc(false);
  }

  async print(): Promise<void> {
    try { await this.api.openPdf(`/documents/${this.doc().id}/pdf`); } catch (e) { this.ui.error(errorMessage(e)); }
  }

  async email(): Promise<void> {
    const to = await this.ui.prompt('E-mail document', 'Recipient e-mail');
    if (!to) return;
    try { await this.api.post(`/documents/${this.doc().id}/email`, { to }); this.ui.ok('E-mail queued.'); } catch (e) { this.ui.error(errorMessage(e)); }
  }

  async sms(): Promise<void> {
    const mobile = await this.ui.prompt('Send SMS', 'Mobile number', { value: this.doc().partyPhone ?? '' });
    if (!mobile) return;
    try { await this.api.post(`/documents/${this.doc().id}/sms`, { mobile }); this.ui.ok('SMS queued.'); } catch (e) { this.ui.error(errorMessage(e)); }
  }

  async cancelDoc(): Promise<void> {
    const reason = await this.ui.prompt('Cancel document', 'Reason for cancellation', { message: 'The bill stays in the void report; its accounts, stock and loyalty effects are reversed.', okText: 'Cancel bill' });
    if (reason === null) return;
    try {
      await this.api.post(`/documents/${this.doc().id}/cancel`, { reason });
      this.ui.ok('Cancelled.');
      await this.loadDoc(this.doc().id);
    } catch (e) { this.ui.error(errorMessage(e)); }
  }

  async deleteDoc(): Promise<void> {
    if (!(await this.ui.confirm('Delete', `Delete ${this.doc().documentNo}?`, { danger: true, okText: 'Delete' }))) return;
    try { await this.api.delete(`/documents/${this.doc().id}`); this.ui.ok('Deleted.'); this.router.navigate(['/documents', this.type()]); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }

  convert(): void {
    const target: VoucherType = this.type() === 'PurchaseOrder' ? 'Purchase' : 'Sales';
    this.router.navigate(['/doc', target, 'new'], { queryParams: { from: this.doc().id } });
  }

  async openOrders(): Promise<void> {
    try {
      const lists = await Promise.all(this.sources().map(t => this.api.get<Paged<DocumentListItem>>('/documents', { type: t, openOrders: true, pageSize: 100 })));
      this.orders.set(lists.flatMap(l => l.items));
      this.ordersModal.set(true);
    } catch (e) { this.ui.error(errorMessage(e)); }
  }

  async importOrder(o: DocumentListItem): Promise<void> {
    this.ordersModal.set(false);
    await this.importOrderById(o.id);
  }

  private async importOrderById(id: number): Promise<void> {
    const src = await this.api.get<TradeDocument>(`/documents/${id}`);
    const d = this.doc();
    d.sourceOrderId = src.id;
    d.partyLedgerId = src.partyLedgerId; d.partyName = src.partyName; d.partyPhone = src.partyPhone; d.partyAddress = src.partyAddress;
    d.partyGstin = src.partyGstin; d.partyStateCode = src.partyStateCode; d.taxType = src.taxType; d.salespersonId = src.salespersonId ?? d.salespersonId;
    d.remarks = `From ${VOUCHER_LABELS[src.voucherType]} ${src.documentNo}`;
    d.orderDetails = src.documentNo;
    d.billDiscount = src.billDiscount; d.billDiscountPercent = src.billDiscountPercent;
    const lookups = await Promise.all(src.lines.map(l => this.api.get<ProductLookup[]>('/products/lookup', { term: l.productCode, exact: true, purchase: this.purchaseSide() })));
    d.lines = src.lines.map((l, i) => {
      const found = lookups[i].find(x => x.batchId === l.batchId) ?? lookups[i][0];
      const nl = found ? lineFromProduct(found, this.type(), { kfCess: this.auth.flag('EnableKfCess'), purchase: this.purchaseSide(), inclusive: l.taxInclusive }) : { ...emptyLine(), productId: l.productId, productName: l.productName };
      nl.qty = l.qty; nl.freeQty = l.freeQty; nl.rate = l.rate; nl.mrp = l.mrp || nl.mrp; nl.discountPercent = l.discountPercent;
      nl.discountAmount = l.discountPercent ? 0 : l.discountAmount; nl.isAltUnit = l.isAltUnit; nl.unitFactor = l.unitFactor; nl.unit = l.unit;
      return nl;
    });
    if (src.partyLedgerId) {
      const parties = await this.api.get<PartyLookup[]>('/ledgers/parties', { type: this.partyType() ?? 'Customer', search: src.partyName ?? '', take: 20 });
      this.party.set(parties.find(p => p.id === src.partyLedgerId) ?? null);
    }
    this.recalc();
    this.ui.info(`Imported ${src.documentNo}. Review and save.`);
  }

  async loadBom(): Promise<void> {
    if (!this.bomId) return;
    try {
      const lines = await this.api.get<{ productId: number; kind: string; qty: number; isAltUnit: boolean }[]>(`/boms/${this.bomId}/scale`, { qty: this.bomQty });
      const d = this.doc();
      d.bomId = this.bomId;
      d.lines = [];
      for (const bl of lines) {
        const prod = await this.api.get<{ code: string }>(`/products/${bl.productId}`);
        const rows = await this.api.get<ProductLookup[]>('/products/lookup', { term: prod.code, exact: true });
        const p = rows.find(r => r.stock > 0) ?? rows[0];
        if (!p) continue;
        const l = lineFromProduct(p, 'Production', { kfCess: false, purchase: false });
        l.direction = (bl.kind === 'FinishedGood' ? 'In' : 'Out') as StockDirection;
        l.qty = bl.qty;
        l.rate = p.cost || p.purchaseRate;
        if (l.direction === 'In') { l.batchId = null; l.batchCode = null; }
        d.lines.push(l);
      }
      this.recalc();
    } catch (e) { this.ui.error(errorMessage(e)); }
  }

  async createParty(): Promise<void> {
    if (!this.np.name.trim()) { this.ui.error('Enter a name.'); return; }
    const gErr = gstinError(this.np.gstin);
    if (gErr) { this.ui.error(gErr); return; }
    if (this.np.gstin && !this.np.stateCode) this.np.stateCode = normalizeGstin(this.np.gstin).substring(0, 2);
    try {
      const groups = await this.api.get<{ id: number; systemCode?: string }[]>('/account-groups');
      const g = groups.find(x => x.systemCode === (this.partyType() === 'Supplier' ? 'SundryCreditors' : 'SundryDebtors'))!;
      const res = await this.api.post<{ id: number }>('/ledgers', {
        name: this.np.name.trim(), mobile: this.np.mobile, stateCode: this.np.stateCode || null, gstin: this.np.gstin || null, address: this.np.address,
        accountGroupId: g.id, partyType: this.partyType(), isActive: true,
      });
      const found = await this.api.get<PartyLookup[]>('/ledgers/parties', { type: this.partyType()!, search: this.np.name.trim(), take: 20 });
      const p = found.find(x => x.id === res.id);
      if (p) { this.pickParty(p); if (this.partyBox) this.partyBox.text = p.name; }
      this.quickParty.set(false);
      this.np = { name: '', mobile: '', stateCode: '', gstin: '', address: '' };
    } catch (e) { this.ui.error(errorMessage(e)); }
  }

  @HostListener('window:keydown', ['$event'])
  keys(e: KeyboardEvent): void {
    if (e.key === 'F2' || (e.ctrlKey && e.key.toLowerCase() === 's')) { e.preventDefault(); this.save(); }
    else if (e.key === 'F3') { e.preventDefault(); this.focusItem(); }
    else if (e.key === 'F4') { e.preventDefault(); this.partyBox?.focus(); }
    else if (e.key === 'F6' && this.postsAccounts()) { e.preventDefault(); this.doc().paymentMode = 'Mixed'; this.openMixed(); }
    else if (e.key === 'F8') { e.preventDefault(); this.newDoc(); }
    else if (e.ctrlKey && e.key.toLowerCase() === 'p' && this.isEdit()) { e.preventDefault(); this.print(); }
  }
}
